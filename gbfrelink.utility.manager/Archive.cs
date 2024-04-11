using System;
using System.Buffers.Binary;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using GBFRDataTools.Entities;

using FlatSharp;
using K4os.Compression.LZ4;
using System.Reflection;

namespace gbfrelink.utility.manager;

public class ArchiveAccessor : IDisposable
{
    private string _gameDir;
    private IndexFile _indexFile;
    private Stream[] _archiveStreams;

    /// <summary>
    /// Initializes an archive accessor.
    /// </summary>
    /// <param name="gameDir">Game directory containing data files such as data.i.</param>
    /// <param name="index">Index file. If null, it will be parsed from the game directory.</param>
    public ArchiveAccessor(string gameDir, IndexFile index = null)
    {
        _gameDir = gameDir;

        if (index is null)
        {
            byte[] indexBuf = File.ReadAllBytes(Path.Combine(gameDir, "data.i"));
            _indexFile = IndexFile.Serializer.Parse(indexBuf);
        }
        else
            _indexFile = index;

        _archiveStreams = new Stream[_indexFile.NumArchives];
    }

    public byte[] GetFileData(string fileName)
    {
        ulong hash = Utils.XXHash64Path(fileName);
        int index = _indexFile.ArchiveFileHashes.BinarySearch(hash);
        if (index < 0)
            throw new FileNotFoundException("File was not found in archive.");

        FileToChunkIndexer indexer = _indexFile.FileToChunkIndexers[index];
        if (indexer.ChunkEntryIndex == -1)
            return Array.Empty<byte>();

        DataChunk chunkEntry = _indexFile.Chunks[(int)indexer.ChunkEntryIndex];
        if (_archiveStreams[chunkEntry.DataFileNumber] is null)
        {
            if (chunkEntry.DataFileNumber > _indexFile.NumArchives)
                throw new Exception("Data file number above number of archives");

            string archivePath = Path.Combine(_gameDir, $"data.{chunkEntry.DataFileNumber}");
            if (!File.Exists(archivePath))
                throw new FileNotFoundException($"Archive file {archivePath} does not exist.");

            _archiveStreams[chunkEntry.DataFileNumber] = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        Stream stream = _archiveStreams[chunkEntry.DataFileNumber];
        stream.Position = (long)chunkEntry.FileOffset;

        byte[] chunk = ArrayPool<byte>.Shared.Rent((int)chunkEntry.Size);
        byte[] decompressedChunk = ArrayPool<byte>.Shared.Rent((int)chunkEntry.UncompressedSize);

        try
        {
            stream.Read(chunk, 0, (int)chunkEntry.Size);

            Span<byte> fileData;
            if (chunkEntry.Size != chunkEntry.UncompressedSize)
            {
                int decoded = LZ4Codec.Decode(chunk, 0, (int)chunkEntry.Size, decompressedChunk, 0, (int)chunkEntry.UncompressedSize);
                if (decoded != chunkEntry.UncompressedSize)
                    throw new Exception("Failed to decompress fully");

                fileData = decompressedChunk.AsSpan((int)indexer.OffsetIntoDecompressedChunk, (int)indexer.FileSize);
            }
            else
                fileData = chunk.AsSpan((int)indexer.OffsetIntoDecompressedChunk, (int)indexer.FileSize);

            return fileData.ToArray();
        }
        catch (Exception ex)
        {
            throw ex;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
            ArrayPool<byte>.Shared.Return(decompressedChunk);
        }
    }

    public void Dispose()
    {
        if (_archiveStreams is not null)
        {
            foreach (Stream archiveStream in _archiveStreams)
                archiveStream.Dispose();
        }
    }
}
