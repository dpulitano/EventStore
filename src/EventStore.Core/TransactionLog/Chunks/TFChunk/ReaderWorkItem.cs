using System.IO;

namespace EventStore.Core.TransactionLog.Chunks.TFChunk {
	internal sealed class ReaderWorkItem {
		public const int BufferSize = 8192;
		public readonly Stream Stream;
		public readonly BinaryReader Reader;
		public readonly bool IsMemory;

		public ReaderWorkItem(Stream stream, BinaryReader reader, bool isMemory) {
			Stream = stream;
			Reader = reader;
			IsMemory = isMemory;
		}
	}
}
