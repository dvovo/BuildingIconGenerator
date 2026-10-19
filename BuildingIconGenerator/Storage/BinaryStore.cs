// Copyright (c) 2026 dvovo
// Licensed under the MIT License. See LICENSE in the repository root.
// https://mods.paradoxplaza.com/authors/dvovo

namespace BuildingIconGenerator.Storage
{
    using System;
    using System.IO;

    /// <summary>
    /// Versioned binary files for the persistent indexes: a 4-byte magic and a format version,
    /// followed by the index's own payload. Writes go to a temporary file that replaces the old
    /// one only once complete, so a crash never leaves a truncated index behind.
    /// </summary>
    internal static class BinaryStore
    {
        /// <summary>Outcome of <see cref="Read"/>.</summary>
        public enum ReadResult
        {
            /// <summary>No file yet.</summary>
            Missing,

            /// <summary>A file of another format or version; it is ignored and rebuilt.</summary>
            Incompatible,

            /// <summary>The payload was read.</summary>
            Loaded,
        }

        /// <summary>Reads the payload when the header matches. Payload exceptions propagate to the caller.</summary>
        public static ReadResult Read(string path, uint magic, int version, Action<BinaryReader> payload)
        {
            if (!File.Exists(path))
            {
                return ReadResult.Missing;
            }

            using (var reader = new BinaryReader(File.OpenRead(path)))
            {
                if (reader.BaseStream.Length < 8 || reader.ReadUInt32() != magic || reader.ReadInt32() != version)
                {
                    return ReadResult.Incompatible;
                }

                payload(reader);
                return ReadResult.Loaded;
            }
        }

        /// <summary>Writes header and payload to a temporary file, then moves it over the target.</summary>
        public static void Write(string path, uint magic, int version, Action<BinaryWriter> payload)
        {
            string temporary = path + ".tmp";

            using (var writer = new BinaryWriter(File.Create(temporary)))
            {
                writer.Write(magic);
                writer.Write(version);
                payload(writer);
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }

            File.Move(temporary, path);
        }
    }
}
