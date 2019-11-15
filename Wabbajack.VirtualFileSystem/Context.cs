﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;
using Alphaleonis.Win32.Filesystem;
using Wabbajack.Common;
using Wabbajack.Common.CSP;
using Directory = Alphaleonis.Win32.Filesystem.Directory;
using File = System.IO.File;
using FileInfo = Alphaleonis.Win32.Filesystem.FileInfo;
using Path = Alphaleonis.Win32.Filesystem.Path;

namespace Wabbajack.VirtualFileSystem
{
    public class Context
    {
        public const ulong FileVersion = 0x02;
        public const string Magic = "WABBAJACK VFS FILE";

        private readonly string _stagingFolder = "vfs_staging";
        public IndexRoot Index { get; private set; } = IndexRoot.Empty;

        public TemporaryDirectory GetTemporaryFolder()
        {
            return new TemporaryDirectory(Path.Combine(_stagingFolder, Guid.NewGuid().ToString()));
        }

        public async Task<IndexRoot> AddRoot(string root)
        {
            if (!Path.IsPathRooted(root))
                throw new InvalidDataException($"Path is not absolute: {root}");

            var filtered = await Index.AllFiles
                .ToChannel()
                .UnorderedPipelineRx(o => o.Where(file => File.Exists(file.Name)))
                .TakeAll();

            var byPath = filtered.ToImmutableDictionary(f => f.Name);

            var results = Channel.Create<VirtualFile>(1024);
            var pipeline = Directory.EnumerateFiles(root, "*", DirectoryEnumerationOptions.Recursive)
                .ToChannel()
                .UnorderedPipeline(results, async f =>
                {
                    if (byPath.TryGetValue(f, out var found))
                    {
                        var fi = new FileInfo(f);
                        if (found.LastModified == fi.LastWriteTimeUtc.Ticks && found.Size == fi.Length)
                            return found;
                    }

                    return await VirtualFile.Analyze(this, null, f, f);
                });

            var allFiles = await results.TakeAll();

            // Should already be done but let's make the async tracker happy
            await pipeline;

            var newIndex = await IndexRoot.Empty.Integrate(filtered.Concat(allFiles).ToList());

            lock (this)
            {
                Index = newIndex;
            }

            return newIndex;
        }

        public async Task WriteToFile(string filename)
        {
            using (var fs = File.OpenWrite(filename))
            using (var bw = new BinaryWriter(fs, Encoding.UTF8, true))
            {
                fs.SetLength(0);

                bw.Write(Encoding.ASCII.GetBytes(Magic));
                bw.Write(FileVersion);
                bw.Write((ulong) Index.AllFiles.Count);

                var sizes = await Index.AllFiles
                    .ToChannel()
                    .UnorderedPipelineSync(f =>
                    {
                        var ms = new MemoryStream();
                        f.Write(ms);
                        return ms;
                    })
                    .Select(async ms =>
                    {
                        var size = ms.Position;
                        ms.Position = 0;
                        bw.Write((ulong) size);
                        await ms.CopyToAsync(fs);
                        return ms.Position;
                    })
                    .TakeAll();
                Utils.Log($"Wrote {fs.Position.ToFileSizeString()} file as vfs cache file {filename}");
            }
        }

        public async Task IntegrateFromFile(string filename)
        {
            using (var fs = File.OpenRead(filename))
            using (var br = new BinaryReader(fs, Encoding.UTF8, true))
            {
                var magic = Encoding.ASCII.GetString(br.ReadBytes(Encoding.ASCII.GetBytes(Magic).Length));
                var fileVersion = br.ReadUInt64();
                if (fileVersion != FileVersion || magic != magic)
                    throw new InvalidDataException("Bad Data Format");

                var numFiles = br.ReadUInt64();

                var input = Channel.Create<byte[]>(1024);
                var pipeline = input.UnorderedPipelineSync(
                        data => VirtualFile.Read(this, data))
                    .TakeAll();

                Utils.Log($"Loading {numFiles} files from {filename}");

                for (ulong idx = 0; idx < numFiles; idx++)
                {
                    var size = br.ReadUInt64();
                    var bytes = new byte[size];
                    await br.BaseStream.ReadAsync(bytes, 0, (int) size);
                    await input.Put(bytes);
                }

                input.Close();

                var files = await pipeline;
                var newIndex = await Index.Integrate(files);
                lock (this)
                {
                    Index = newIndex;
                }
            }
        }

        public async Task<Action> Stage(IEnumerable<VirtualFile> files)
        {
            var grouped = files.SelectMany(f => f.FilesInFullPath)
                .Distinct()
                .Where(f => f.Parent != null)
                .GroupBy(f => f.Parent)
                .OrderBy(f => f.Key?.NestingFactor ?? 0)
                .ToList();

            var paths = new List<string>();

            foreach (var group in grouped)
            {
                var tmpPath = Path.Combine(_stagingFolder, Guid.NewGuid().ToString());
                await FileExtractor.ExtractAll(group.Key.StagedPath, tmpPath);
                paths.Add(tmpPath);
                foreach (var file in group)
                    file.StagedPath = Path.Combine(tmpPath, file.Name);
            }

            return () =>
            {
                paths.Do(p =>
                {
                    if (Directory.Exists(p))
                        Directory.Delete(p, true, true);
                });
            };
        }

        public List<PortableFile> GetPortableState(IEnumerable<VirtualFile> files)
        {
            return files.SelectMany(f => f.FilesInFullPath)
                .Distinct()
                .Select(f => new PortableFile
                {
                    Name = f.Parent != null ? f.Name : null,
                    Hash = f.Hash,
                    ParentHash = f.Parent?.Hash,
                    Size = f.Size
                }).ToList();
        }

        public async Task IntegrateFromPortable(List<PortableFile> state, Dictionary<string, string> links)
        {
            var indexedState = state.GroupBy(f => f.ParentHash)
                .ToDictionary(f => f.Key ?? "", f => (IEnumerable<PortableFile>) f);
            var parents = await indexedState[""]
                .ToChannel()
                .UnorderedPipelineSync(f => VirtualFile.CreateFromPortable(this, indexedState, links, f))
                .TakeAll();

            var newIndex = await Index.Integrate(parents);
            lock (this)
            {
                Index = newIndex;
            }
        }

        public async Task<DisposableList<VirtualFile>> StageWith(IEnumerable<VirtualFile> files)
        {
            return new DisposableList<VirtualFile>(await Stage(files), files);
        }


        #region KnownFiles

        private List<KnownFile> _knownFiles = new List<KnownFile>();
        public void AddKnown(IEnumerable<KnownFile> known)
        {
            _knownFiles.AddRange(known);
        }

        public async Task BackfillMissing()
        {
            var newFiles = _knownFiles.Where(f => f.Paths.Length == 1)
                                       .GroupBy(f => f.Hash)
                                       .ToDictionary(f => f.Key, s => new VirtualFile()
                                       {
                                           Name = s.First().Paths[0],
                                           Hash = s.First().Hash,
                                           Context = this
                                       });

            var parentchild = new Dictionary<(VirtualFile, string), VirtualFile>();

            void BackFillOne(KnownFile file)
            {
                var parent = newFiles[file.Paths[0]];
                foreach (var path in file.Paths.Skip(1))
                {
                    if (parentchild.TryGetValue((parent, path), out var foundParent))
                    {
                        parent = foundParent;
                        continue;
                    }

                    var nf = new VirtualFile();
                    nf.Name = path;
                    nf.Parent = parent;
                    parent.Children = parent.Children.Add(nf);
                    parentchild.Add((parent, path), nf);
                    parent = nf;
                }
            }
            _knownFiles.Where(f => f.Paths.Length > 1).Do(BackFillOne);

            var newIndex = await Index.Integrate(newFiles.Values.ToList());

            lock (this)
                Index = newIndex;

            _knownFiles = new List<KnownFile>();

        }
        
        #endregion

    }

    public class KnownFile
    {
        public string[] Paths { get; set; }
        public string Hash { get; set; }
    }

    public class DisposableList<T> : List<T>, IDisposable
    {
        private Action _unstage;

        public DisposableList(Action unstage, IEnumerable<T> files) : base(files)
        {
            _unstage = unstage;
        }

        public void Dispose()
        {
            _unstage();
        }
    }

    public class IndexRoot
    {
        public static IndexRoot Empty = new IndexRoot();

        public IndexRoot(ImmutableList<VirtualFile> aFiles,
            ImmutableDictionary<string, VirtualFile> byFullPath,
            ImmutableDictionary<string, ImmutableStack<VirtualFile>> byHash,
            ImmutableDictionary<string, VirtualFile> byRoot,
            ImmutableDictionary<string, ImmutableStack<VirtualFile>> byName)
        {
            AllFiles = aFiles;
            ByFullPath = byFullPath;
            ByHash = byHash;
            ByRootPath = byRoot;
            ByName = byName;
        }

        public IndexRoot()
        {
            AllFiles = ImmutableList<VirtualFile>.Empty;
            ByFullPath = ImmutableDictionary<string, VirtualFile>.Empty;
            ByHash = ImmutableDictionary<string, ImmutableStack<VirtualFile>>.Empty;
            ByRootPath = ImmutableDictionary<string, VirtualFile>.Empty;
            ByName = ImmutableDictionary<string, ImmutableStack<VirtualFile>>.Empty;
        }


        public ImmutableList<VirtualFile> AllFiles { get; }
        public ImmutableDictionary<string, VirtualFile> ByFullPath { get; }
        public ImmutableDictionary<string, ImmutableStack<VirtualFile>> ByHash { get; }
        public ImmutableDictionary<string, ImmutableStack<VirtualFile>> ByName { get; set; }
        public ImmutableDictionary<string, VirtualFile> ByRootPath { get; }

        public async Task<IndexRoot> Integrate(List<VirtualFile> files)
        {
            var allFiles = AllFiles.Concat(files).GroupBy(f => f.Name).Select(g => g.Last()).ToImmutableList();

            var byFullPath = Task.Run(() =>
                allFiles.SelectMany(f => f.ThisAndAllChildren)
                    .ToImmutableDictionary(f => f.FullPath));

            var byHash = Task.Run(() =>
                allFiles.SelectMany(f => f.ThisAndAllChildren)
                    .Where(f => f.Hash != null)
                    .ToGroupedImmutableDictionary(f => f.Hash));

            var byName = Task.Run(() =>
                allFiles.SelectMany(f => f.ThisAndAllChildren)
                    .ToGroupedImmutableDictionary(f => f.Name));

            var byRootPath = Task.Run(() => allFiles.ToImmutableDictionary(f => f.Name));

            return new IndexRoot(allFiles,
                await byFullPath,
                await byHash,
                await byRootPath,
                await byName);
        }

        public VirtualFile FileForArchiveHashPath(string[] argArchiveHashPath)
        {
            var cur = ByHash[argArchiveHashPath[0]].First(f => f.Parent == null);
            return argArchiveHashPath.Skip(1).Aggregate(cur, (current, itm) => ByName[itm].First(f => f.Parent == current));
        }
    }

    public class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory(string name)
        {
            FullName = name;
        }

        public string FullName { get; }

        public void Dispose()
        {
            Directory.Delete(FullName, true, true);
        }
    }
}