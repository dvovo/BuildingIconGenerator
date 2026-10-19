// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo

namespace BuildingIconGenerator.Textures
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.IO;
    using System.Reflection;
    using System.Threading;
    using Colossal.Compression;
    using Colossal.IO.AssetDatabase;
    using UnityEngine;
    using UnityEngine.Experimental.Rendering;

    /// <summary>
    /// Reads virtual-texturing (VT) data the way the game's own streamer does, instead of through
    /// the debug loaders (<c>VTSurfaceAsset.Load</c> / <c>VTTextureAsset.Load</c>), which decode every
    /// tile of every mip at full resolution on the main thread.
    ///
    /// <para>On-disk layout (from <c>VTTextureAsset</c>, <c>VTSurfaceDataHandler</c> and the game's
    /// tile streamer):</para>
    /// <list type="bullet">
    /// <item>tiles of 512 px plus 8 px of padding per side, BC-compressed, each zstd-compressed on
    /// its own (0x7FFFFFFF marker), followed by a quarter-size trilinear copy;</item>
    /// <item>tiles stored mip by mip, row-major inside a mip;</item>
    /// <item>a <c>VTSurfaceAsset</c> tile packs every pre-processed layer of its stack (albedo,
    /// normal, mask, control...) one after another in the same compressed chunk;</item>
    /// <item>mips below 512 px live in a resident mid-mip file and a raw tail.</item>
    /// </list>
    ///
    /// <para>An icon needs ONE mip level: the 1024 level of a 4096 texture is 4 tiles instead of 85.
    /// Planning (headers, tile tables: small reads) runs on the main thread; reading the tiles and
    /// zstd decompression run on background workers (the same <c>DecompressZstdWithMarker</c> the
    /// game's decompression jobs use); the main thread only creates textures from ready blocks
    /// (compressed upload, no transcoding).</para>
    /// </summary>
    internal static class FastVtLoader
    {
        private const int kPadPixels = 8;
        private const BindingFlags kAny = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public sealed class Layer
        {
            public string Property;
            public GraphicsFormat Format;
            public int Slot;
            public int Width;
            public int Height;
            public byte[] Pixels;
            public bool MipChain;
            public TextureWrapMode Wrap = TextureWrapMode.Clamp;

            /// <summary>Content key (texture GUID + level) of a per-texture read: shareable between surfaces.</summary>
            public string Key;

            /// <summary>Set by Finish: the texture made from this layer and its size.</summary>
            public Texture2D Texture;
            public long Bytes;
        }

        public sealed class Tile
        {
            public long Offset;
            public int Length;
            public int Row;
            public int Col;
            public byte[] Data;
        }

        public sealed class Group
        {
            public string Path;
            public int TileSize;
            public int BlockWidth;
            public int BlockHeight;
            public int BlockBytes;
            public int ChunkStride;
            public int Width;
            public int Height;
            public readonly List<Tile> Tiles = new List<Tile>();
            public readonly List<Layer> Layers = new List<Layer>();
        }

        public sealed class Job
        {
            public string Name;
            public int Target;
            public readonly List<Group> Groups = new List<Group>();
            public readonly List<Layer> Direct = new List<Layer>();
            public readonly List<KeyValuePair<string, VTTextureAsset>> Slow = new List<KeyValuePair<string, VTTextureAsset>>();
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public volatile bool Started;
            public string Error;
            public long CompressedBytes;
            public int TileCount;

            /// <summary>Some texture GUID of the surface was unknown to the database (pack not loaded).</summary>
            public bool MissingTextures;

            /// <summary>Properties served by an already resident shared texture (property, content key).</summary>
            public readonly List<KeyValuePair<string, string>> Shared = new List<KeyValuePair<string, string>>();

            public bool IsEmpty => Groups.Count == 0 && Direct.Count == 0 && Slow.Count == 0 && Shared.Count == 0;
        }

        // ---------------------------------------------------------------- reflection (private game members)

        private static bool s_Init;
        private static bool s_Available;
        private static FieldInfo s_Data;
        private static FieldInfo s_Footer;
        private static FieldInfo s_FooterLists;
        private static MethodInfo s_TexReadHeader;
        private static FieldInfo s_TexTileSize;
        private static readonly Dictionary<(Type, string), FieldInfo> s_Fields = new Dictionary<(Type, string), FieldInfo>();

        public static string InitError { get; private set; }

        public static bool Available
        {
            get
            {
                if (!s_Init)
                {
                    s_Init = true;

                    try
                    {
                        var handler = typeof(VTSurfaceDataHandler);
                        s_Data = handler.GetField("m_Data", kAny);
                        s_Footer = handler.GetField("m_FooterData", kAny);
                        s_FooterLists = s_Footer?.FieldType.GetField("compressedTileOffsetPerStack", kAny);
                        var texture = typeof(VTTextureAsset);
                        s_TexReadHeader = texture.GetMethod("ReadHeader", kAny, null, new[] { typeof(BinaryReader) }, null);
                        s_TexTileSize = texture.GetField("m_TileSize", kAny);
                        s_Available = s_Data != null && s_Footer != null && s_FooterLists != null && s_TexReadHeader != null && s_TexTileSize != null;

                        if (!s_Available)
                        {
                            InitError = $"members not found: data {s_Data != null}, footer {s_Footer != null}, lists {s_FooterLists != null}, "
                                        + $"readHeader {s_TexReadHeader != null}, tileSize {s_TexTileSize != null}";
                        }
                    }
                    catch (Exception ex)
                    {
                        InitError = $"{ex.GetType().Name}: {ex.Message}";
                    }
                }

                return s_Available;
            }
        }

        private static object Get(object boxed, string name)
        {
            var key = (boxed.GetType(), name);

            if (!s_Fields.TryGetValue(key, out var field))
            {
                field = key.Item1.GetField(name, kAny);
                s_Fields[key] = field;
            }

            if (field == null)
            {
                throw new MissingFieldException(key.Item1.Name, name);
            }

            return field.GetValue(boxed);
        }

        private static int GetInt(object boxed, string name) => Convert.ToInt32(Get(boxed, name));

        // ---------------------------------------------------------------- planning (main thread)

        /// <summary>
        /// Tiled mip levels (the order of AtlassingUtils.TextureRelativeTileIndex, which the game's
        /// streamer uses: mip by mip, row-major, (w/T) x (h/T) tiles, non-square included): the
        /// level to read for this target size.
        /// </summary>
        private static bool ChooseLevel(int width, int height, int tileSize, int target, int tileCount,
            out int levelWidth, out int levelHeight, out int first, out int across, out int down, out string reason)
        {
            levelWidth = levelHeight = first = across = down = 0;
            reason = null;
            int levels = 0;
            int total = 0;

            for (int k = 0; (width >> k) >= tileSize && (height >> k) >= tileSize; k++)
            {
                levels++;
                total += ((width >> k) / tileSize) * ((height >> k) / tileSize);
            }

            if (levels == 0 || total != tileCount || width % tileSize != 0 || height % tileSize != 0)
            {
                reason = $"tile count {tileCount} != {total} expected for {width}x{height}";
                return false;
            }

            int want = Math.Max(tileSize, Math.Min(target, Math.Max(width, height)));
            int level = 0;

            while (level + 1 < levels && Math.Max(width, height) >> (level + 1) >= want)
            {
                level++;
            }

            for (int k = 0; k < level; k++)
            {
                first += ((width >> k) / tileSize) * ((height >> k) / tileSize);
            }

            levelWidth = width >> level;
            levelHeight = height >> level;
            across = levelWidth / tileSize;
            down = levelHeight / tileSize;
            return true;
        }

        private static readonly Dictionary<string, bool> s_PathUsable = new Dictionary<string, bool>();

        /// <summary>
        /// Where the worker reads from: the packaged file itself (AsyncReadDescriptor, as the game's
        /// AsyncReadManager does), checked once per file against the asset stream; otherwise the
        /// compressed bytes are read here, on the main thread.
        /// </summary>
        private static string ResolvePath(AssetData asset, Stream stream, long probeOffset, int probeLength, out long baseOffset)
        {
            baseOffset = 0;

            try
            {
                var descriptor = asset.GetAsyncReadDescriptor();
                string path = descriptor.path;

                if (string.IsNullOrEmpty(path))
                {
                    return null;
                }

                baseOffset = descriptor.offset;

                if (!s_PathUsable.TryGetValue(path, out bool usable))
                {
                    usable = false;

                    try
                    {
                        int n = Math.Min(64, probeLength);
                        var fromStream = new byte[n];
                        long keep = stream.Position;
                        stream.Position = probeOffset;
                        ReadExactly(stream, fromStream, n);
                        stream.Position = keep;

                        var fromFile = new byte[n];

                        using (var file = OpenShared(path))
                        {
                            file.Position = baseOffset + probeOffset;
                            ReadExactly(file, fromFile, n);
                        }

                        usable = true;

                        for (int i = 0; i < n; i++)
                        {
                            if (fromStream[i] != fromFile[i])
                            {
                                usable = false;
                                break;
                            }
                        }
                    }
                    catch
                    {
                        usable = false;
                    }

                    s_PathUsable[path] = usable;
                }

                return usable ? path : null;
            }
            catch
            {
                return null;
            }
        }

        private static FileStream OpenShared(string path)
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
        }

        private static void ReadExactly(Stream stream, byte[] buffer, int count)
        {
            int read = 0;

            while (read < count)
            {
                int n = stream.Read(buffer, read, count - read);

                if (n <= 0)
                {
                    throw new EndOfStreamException();
                }

                read += n;
            }
        }

        private static void AddTiles(Group group, Job job, IList ends, long dataStart, int first, int across, int down, Stream stream, string path, long baseOffset)
        {
            for (int t = 0; t < across * down; t++)
            {
                int index = first + t;
                long previous = index == 0 ? 0 : Convert.ToInt64(ends[index - 1]);
                long end = Convert.ToInt64(ends[index]);
                var tile = new Tile
                {
                    Offset = dataStart + previous,
                    Length = (int)(end - previous),
                    Row = t / across,
                    Col = t % across,
                };

                if (path == null)
                {
                    tile.Data = new byte[tile.Length];
                    stream.Position = tile.Offset;
                    ReadExactly(stream, tile.Data, tile.Length);
                }
                else
                {
                    tile.Offset += baseOffset;
                }

                job.CompressedBytes += tile.Length;
                job.TileCount++;
                group.Tiles.Add(tile);
            }

            group.Path = path;
        }

        private static bool BlockInfo(GraphicsFormat format, out int width, out int height, out int bytes)
        {
            width = (int)GraphicsFormatUtility.GetBlockWidth(format);
            height = (int)GraphicsFormatUtility.GetBlockHeight(format);
            bytes = (int)GraphicsFormatUtility.GetBlockSize(format);
            return width > 0 && height > 0 && bytes > 0 && GraphicsFormatUtility.IsCompressedFormat(format);
        }

        /// <summary>A VTSurfaceAsset: every pre-processed layer of every stack, one mip level.</summary>
        public static bool PlanSurface(VTSurfaceAsset asset, Func<int, int, string> propertyOf, int target, Job job, out string reason)
        {
            reason = null;

            if (!Available)
            {
                reason = InitError;
                return false;
            }

            using (var stream = asset.GetReadStream())
            using (var reader = new BinaryReader(stream))
            {
                var handler = new VTSurfaceDataHandler();

                try
                {
                    handler.ReadHeader(reader);
                    handler.ReadFooter(reader);
                    object data = s_Data.GetValue(handler);
                    object footer = s_Footer.GetValue(handler);
                    var perStack = s_FooterLists.GetValue(footer) as IList;
                    int tileSize = GetInt(data, "m_TileSize");
                    int stacks = GetInt(data, "m_NbActiveStacks");
                    string path = null;
                    long baseOffset = 0;
                    bool resolved = false;

                    for (int s = 0; s < stacks; s++)
                    {
                        object stack = Get(data, "m_StackData" + s);
                        int width = GetInt(stack, "TextureWidth");
                        int height = GetInt(stack, "TextureHeight");
                        int tiles = GetInt(stack, "NbTiles");
                        int layers = GetInt(stack, "m_NbActiveLayers");
                        long tilesData = GetInt(stack, "TilesDataOffset");

                        if (tiles <= 0)
                        {
                            continue;
                        }

                        var ends = perStack != null && s < perStack.Count ? perStack[s] as IList : null;

                        if (ends == null || ends.Count < tiles)
                        {
                            reason = "no tile table";
                            return false;
                        }

                        var group = new Group { TileSize = tileSize };
                        int slot = 0;

                        for (int l = 0; l < layers; l++)
                        {
                            object layer = Get(data, $"m_LayerData{s}_{l}");

                            if (!Convert.ToBoolean(Get(layer, "isPreProcessed")))
                            {
                                continue;
                            }

                            if (Convert.ToInt32(Get(layer, "preProcessedFormat")) != 0)
                            {
                                reason = "pre-processed format " + Convert.ToInt32(Get(layer, "preProcessedFormat"));
                                return false;
                            }

                            var format = (GraphicsFormat)Convert.ToInt32(Get(layer, "format"));

                            if (!BlockInfo(format, out int bw, out int bh, out int bb))
                            {
                                reason = "format " + format;
                                return false;
                            }

                            if (group.Layers.Count > 0 && (bw != group.BlockWidth || bh != group.BlockHeight || bb != group.BlockBytes))
                            {
                                reason = "mixed block sizes";
                                return false;
                            }

                            int tileBytes = GetInt(layer, "tileSizeInBytes");
                            group.BlockWidth = bw;
                            group.BlockHeight = bh;
                            group.BlockBytes = bb;
                            group.ChunkStride = tileBytes + (tileBytes / 4);
                            string property = propertyOf(s, l);

                            if (property != null)
                            {
                                group.Layers.Add(new Layer { Property = property, Format = format, Slot = slot, Wrap = TextureWrapMode.Clamp });
                            }

                            slot++;
                        }

                        if (group.Layers.Count == 0)
                        {
                            continue;
                        }

                        if (!ChooseLevel(width, height, tileSize, target, tiles, out int levelWidth, out int levelHeight, out int first, out int across, out int down, out reason))
                        {
                            return false;
                        }

                        group.Width = levelWidth;
                        group.Height = levelHeight;

                        if (!resolved)
                        {
                            long previous = first == 0 ? 0 : Convert.ToInt64(ends[first - 1]);
                            int length = (int)(Convert.ToInt64(ends[first]) - previous);
                            path = ResolvePath(asset, stream, tilesData + previous, length, out baseOffset);
                            resolved = true;
                        }

                        AddTiles(group, job, ends, tilesData, first, across, down, stream, path, baseOffset);

                        foreach (var layer in group.Layers)
                        {
                            layer.Width = levelWidth;
                            layer.Height = levelHeight;
                        }

                        job.Groups.Add(group);
                    }

                    return true;
                }
                finally
                {
                    try
                    {
                        handler.Dispose();
                    }
                    catch
                    {
                    }
                }
            }
        }

        /// <summary>A VTTextureAsset (props: the per-texture GUID route), one mip level.</summary>
        public static bool PlanTexture(VTTextureAsset asset, string property, int target, Job job, out string reason, string key = null)
        {
            reason = null;

            if (!Available)
            {
                reason = InitError;
                return false;
            }

            using (var stream = asset.GetReadStream())
            using (var reader = new BinaryReader(stream))
            {
                s_TexReadHeader.Invoke(asset, new object[] { reader });
                int tileSize = Convert.ToInt32(s_TexTileSize.GetValue(asset));
                int width = Convert.ToInt32(asset.width);
                int height = Convert.ToInt32(asset.height);
                int mipCount = reader.ReadByte();
                var format = (GraphicsFormat)reader.ReadByte();
                reader.ReadByte(); // filter
                var wrap = (TextureWrapMode)reader.ReadByte();
                reader.ReadByte(); // aniso
                int tailLength = reader.ReadInt32();
                long tailStart = stream.Position;
                stream.Position = tailStart + tailLength;
                int tiles = reader.ReadInt32();

                if (tiles < 0 || tiles > 100000)
                {
                    reason = "bad tile count " + tiles;
                    return false;
                }

                var ends = new int[tiles];

                for (int i = 0; i < tiles; i++)
                {
                    ends[i] = reader.ReadInt32();
                }

                long dataStart = stream.Position;

                if (!BlockInfo(format, out int bw, out int bh, out int bb))
                {
                    reason = "format " + format;
                    return false;
                }

                if (tiles == 0)
                {
                    // Small texture: the whole chain is the raw tail - no decompression at all.
                    long chain = ChainBytes(width, height, bw, bh, bb, mipCount);
                    int full = 1 + (int)Math.Floor(Math.Log(Math.Max(width, height), 2));

                    if (tailLength <= 0 || chain != tailLength || (mipCount != 1 && mipCount != full))
                    {
                        reason = $"no tiles, tail {tailLength} != chain {chain} ({width}x{height}, {mipCount} mips)";
                        return false;
                    }

                    var bytes = new byte[tailLength];
                    stream.Position = tailStart;
                    ReadExactly(stream, bytes, tailLength);
                    job.Direct.Add(new Layer { Property = property, Format = format, Width = width, Height = height, Pixels = bytes, MipChain = mipCount > 1, Wrap = wrap, Key = key });
                    return true;
                }

                if (!ChooseLevel(width, height, tileSize, target, tiles, out int levelWidth, out int levelHeight, out int first, out int across, out int down, out reason))
                {
                    return false;
                }

                var group = new Group
                {
                    TileSize = tileSize,
                    BlockWidth = bw,
                    BlockHeight = bh,
                    BlockBytes = bb,
                    ChunkStride = 0,
                    Width = levelWidth,
                    Height = levelHeight,
                };

                group.Layers.Add(new Layer { Property = property, Format = format, Slot = 0, Width = levelWidth, Height = levelHeight, Wrap = wrap, Key = key });
                long previous = first == 0 ? 0 : ends[first - 1];
                string path = ResolvePath(asset, stream, dataStart + previous, ends[first] - (int)previous, out long baseOffset);
                AddTiles(group, job, ends, dataStart, first, across, down, stream, path, baseOffset);
                job.Groups.Add(group);
                return true;
            }
        }

        private static long ChainBytes(int width, int height, int bw, int bh, int bb, int mips)
        {
            long total = 0;

            for (int m = 0; m < mips; m++)
            {
                int w = Math.Max(1, width >> m);
                int h = Math.Max(1, height >> m);
                total += (long)((w + bw - 1) / bw) * ((h + bh - 1) / bh) * bb;
            }

            return total;
        }

        // ---------------------------------------------------------------- plan blueprints

        /// <summary>A plan can be replayed when its tiles are read by the workers (no bytes held).</summary>
        public static bool Cacheable(Job job)
        {
            foreach (var group in job.Groups)
            {
                if (group.Path == null)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>A fresh job with the same plan (byte arrays shared: never written after planning).</summary>
        public static Job Clone(Job source)
        {
            var job = new Job
            {
                Name = source.Name,
                Target = source.Target,
                CompressedBytes = source.CompressedBytes,
                TileCount = source.TileCount,
                MissingTextures = source.MissingTextures,
            };

            foreach (var group in source.Groups)
            {
                var copy = new Group
                {
                    Path = group.Path,
                    TileSize = group.TileSize,
                    BlockWidth = group.BlockWidth,
                    BlockHeight = group.BlockHeight,
                    BlockBytes = group.BlockBytes,
                    ChunkStride = group.ChunkStride,
                    Width = group.Width,
                    Height = group.Height,
                };

                foreach (var tile in group.Tiles)
                {
                    copy.Tiles.Add(new Tile { Offset = tile.Offset, Length = tile.Length, Row = tile.Row, Col = tile.Col, Data = tile.Data });
                }

                foreach (var layer in group.Layers)
                {
                    copy.Layers.Add(CopyLayer(layer));
                }

                job.Groups.Add(copy);
            }

            foreach (var layer in source.Direct)
            {
                job.Direct.Add(CopyLayer(layer));
            }

            job.Slow.AddRange(source.Slow);
            return job;
        }

        private static Layer CopyLayer(Layer layer)
        {
            return new Layer
            {
                Property = layer.Property,
                Format = layer.Format,
                Slot = layer.Slot,
                Width = layer.Width,
                Height = layer.Height,
                Pixels = layer.Pixels,
                MipChain = layer.MipChain,
                Wrap = layer.Wrap,
                Key = layer.Key,
            };
        }

        // ---------------------------------------------------------------- decoding (workers)

        private static readonly object s_Lock = new object();
        private static readonly List<Job> s_Queue = new List<Job>();
        private static Thread[] s_Workers;

        public static int WorkerCount => s_Workers?.Length ?? 0;

        /// <summary>Queues the job for the background workers (below-normal priority: the game stays smooth).</summary>
        public static void Start(Job job)
        {
            lock (s_Lock)
            {
                if (s_Workers == null)
                {
                    int count = Math.Max(1, Math.Min(8, Environment.ProcessorCount - 2));
                    s_Workers = new Thread[count];

                    for (int i = 0; i < count; i++)
                    {
                        s_Workers[i] = new Thread(WorkerLoop)
                        {
                            IsBackground = true,
                            Priority = System.Threading.ThreadPriority.BelowNormal,
                            Name = "BuildingIconGenerator decode " + i,
                        };
                        s_Workers[i].Start();
                    }
                }

                s_Queue.Add(job);
                Monitor.Pulse(s_Lock);
            }
        }

        /// <summary>Blocks until the job is decoded; a job still queued is decoded right here (no waiting behind others).</summary>
        public static void Wait(Job job)
        {
            bool mine = false;

            lock (s_Lock)
            {
                if (!job.Started && s_Queue.Remove(job))
                {
                    job.Started = true;
                    mine = true;
                }
            }

            if (mine)
            {
                Run(job);
            }

            job.Done.Wait();
        }

        /// <summary>Drops a job nobody needs any more (still queued: never decoded).</summary>
        public static void Cancel(Job job)
        {
            lock (s_Lock)
            {
                if (!job.Started && s_Queue.Remove(job))
                {
                    job.Started = true;
                    job.Error = "cancelled";
                    job.Done.Set();
                }
            }
        }

        private static void WorkerLoop()
        {
            while (true)
            {
                Job job;

                lock (s_Lock)
                {
                    while (s_Queue.Count == 0)
                    {
                        Monitor.Wait(s_Lock);
                    }

                    job = s_Queue[0];
                    s_Queue.RemoveAt(0);
                    job.Started = true;
                }

                Run(job);
            }
        }

        private static void Run(Job job)
        {
            try
            {
                Decode(job);
            }
            catch (Exception ex)
            {
                job.Error = $"{ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                job.Done.Set();
            }
        }

        private static void Decode(Job job)
        {
            FileStream file = null;
            string open = null;

            try
            {
                foreach (var group in job.Groups)
                {
                    int bw = group.BlockWidth;
                    int bh = group.BlockHeight;
                    int bb = group.BlockBytes;
                    int padded = (group.TileSize + (2 * kPadPixels)) / bw;
                    int paddedRows = (group.TileSize + (2 * kPadPixels)) / bh;
                    int padW = kPadPixels / bw;
                    int padH = kPadPixels / bh;
                    int inner = group.TileSize / bw;
                    int innerRows = group.TileSize / bh;
                    int rowBytes = inner * bb;
                    int dstRow = group.Width / bw;
                    int tileBytes = padded * paddedRows * bb;

                    foreach (var layer in group.Layers)
                    {
                        layer.Pixels = new byte[dstRow * (group.Height / bh) * bb];
                    }

                    foreach (var tile in group.Tiles)
                    {
                        byte[] compressed = tile.Data;

                        if (compressed == null)
                        {
                            if (file == null || open != group.Path)
                            {
                                file?.Dispose();
                                file = OpenShared(group.Path);
                                open = group.Path;
                            }

                            compressed = new byte[tile.Length];
                            file.Position = tile.Offset;
                            ReadExactly(file, compressed, tile.Length);
                        }

                        byte[] chunk = CompressionUtils.DecompressZstdWithMarker(compressed);
                        tile.Data = null;

                        foreach (var layer in group.Layers)
                        {
                            int start = layer.Slot * group.ChunkStride;

                            if (start + tileBytes > chunk.Length)
                            {
                                throw new InvalidDataException($"tile chunk {chunk.Length} B, layer {layer.Slot} needs {start + tileBytes}");
                            }

                            for (int r = 0; r < innerRows; r++)
                            {
                                int src = start + ((((padH + r) * padded) + padW) * bb);
                                int dst = ((((tile.Row * innerRows) + r) * dstRow) + (tile.Col * inner)) * bb;
                                Buffer.BlockCopy(chunk, src, layer.Pixels, dst, rowBytes);
                            }
                        }
                    }
                }
            }
            finally
            {
                file?.Dispose();
            }
        }

        // ---------------------------------------------------------------- textures (main thread)

        /// <summary>Main thread: decoded blocks -> Texture2D (compressed upload, CPU copy released).</summary>
        public static Dictionary<string, Texture> Finish(Job job, List<Texture2D> owned, out long bytes)
        {
            bytes = 0;
            var result = new Dictionary<string, Texture>();

            foreach (var group in job.Groups)
            {
                foreach (var layer in group.Layers)
                {
                    bytes += Create(layer, result, owned, job.Name);
                }
            }

            foreach (var layer in job.Direct)
            {
                bytes += Create(layer, result, owned, job.Name);
            }

            foreach (var pair in job.Slow)
            {
                try
                {
                    var texture = pair.Value.Load();

                    if (texture != null && !result.ContainsKey(pair.Key))
                    {
                        result[pair.Key] = texture;
                    }
                }
                catch
                {
                }
            }

            return result;
        }

        private static long Create(Layer layer, Dictionary<string, Texture> result, List<Texture2D> owned, string name)
        {
            if (layer.Pixels == null || result.ContainsKey(layer.Property))
            {
                return 0;
            }

            var texture = new Texture2D(layer.Width, layer.Height, layer.Format, layer.MipChain ? TextureCreationFlags.MipChain : TextureCreationFlags.None)
            {
                hideFlags = HideFlags.HideAndDontSave,
                name = name + "|" + layer.Property,
                wrapMode = layer.Wrap,
                filterMode = FilterMode.Bilinear,
                anisoLevel = 4,
            };

            texture.LoadRawTextureData(layer.Pixels);
            texture.Apply(false, true);
            long size = layer.Pixels.Length;
            layer.Pixels = null;
            layer.Texture = texture;
            layer.Bytes = size;
            result[layer.Property] = texture;
            owned.Add(texture);
            return size;
        }
    }
}
