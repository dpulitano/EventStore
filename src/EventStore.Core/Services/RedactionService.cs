﻿using System;
using System.IO;
using System.Linq;
using System.Threading;
using EventStore.Common.Utils;
using EventStore.Core.Bus;
using EventStore.Core.Data;
using EventStore.Core.Data.Redaction;
using EventStore.Core.Exceptions;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services.Storage.ReaderIndex;
using EventStore.Core.TransactionLog.Chunks;
using EventStore.Core.TransactionLog.Chunks.TFChunk;
using Serilog;

namespace EventStore.Core.Services {
	public class RedactionService<TStreamId> :
		IHandle<RedactionMessage.GetEventPosition>,
		IHandle<RedactionMessage.SwitchChunkLock>,
		IHandle<RedactionMessage.SwitchChunk>,
		IHandle<RedactionMessage.SwitchChunkUnlock> {

		private readonly TFChunkDb _db;
		private readonly IReadIndex<TStreamId> _readIndex;
		private readonly SemaphoreSlim _switchChunksSemaphore;

		private const string NewChunkFileExtension = ".tmp";

		public RedactionService(
			TFChunkDb db,
			IReadIndex<TStreamId> readIndex,
			SemaphoreSlim switchChunksSemaphore) {
			Ensure.NotNull(db, nameof(db));
			Ensure.NotNull(readIndex, nameof(readIndex));
			Ensure.NotNull(switchChunksSemaphore, nameof(switchChunksSemaphore));

			_db = db;
			_readIndex = readIndex;
			_switchChunksSemaphore = switchChunksSemaphore;
		}

		public void Handle(RedactionMessage.GetEventPosition message) {
			ThreadPool.QueueUserWorkItem(_ => {
				try {
					GetEventPosition(message.EventStreamId, message.EventNumber, message.Envelope);
				} catch (Exception ex) {
					Log.Error(ex, "An error has occurred when getting position for stream: {stream}, event number: {eventNumber}.",
						message.EventStreamId, message.EventNumber);
					message.Envelope.ReplyWith(
						new RedactionMessage.GetEventPositionCompleted(GetEventPositionResult.UnexpectedError, Array.Empty<EventPosition>()));
				}
			});
		}

		private void GetEventPosition(string streamName, long eventNumber, IEnvelope envelope) {
			var streamId = _readIndex.GetStreamId(streamName);
			var result = _readIndex.ReadEventInfo_KeepDuplicates(streamId, eventNumber);

			var eventPositions =
				result.EventInfos.Select(x => new EventPosition(x.LogPosition)).ToArray();

			envelope.ReplyWith(
				new RedactionMessage.GetEventPositionCompleted(GetEventPositionResult.Success, eventPositions));
		}

		public void Handle(RedactionMessage.SwitchChunkLock message) {
			if (_switchChunksSemaphore.Wait(TimeSpan.Zero))
				message.Envelope.ReplyWith(
					new RedactionMessage.SwitchChunkLockCompleted(SwitchChunkLockResult.Success));
			else
				message.Envelope.ReplyWith(
					new RedactionMessage.SwitchChunkLockCompleted(SwitchChunkLockResult.Failed));
		}

		public void Handle(RedactionMessage.SwitchChunkUnlock message) {
			try {
				_switchChunksSemaphore.Release();
				message.Envelope.ReplyWith(
					new RedactionMessage.SwitchChunkUnlockCompleted(SwitchChunkUnlockResult.Success));
			} catch (SemaphoreFullException) {
				message.Envelope.ReplyWith(
					new RedactionMessage.SwitchChunkUnlockCompleted(SwitchChunkUnlockResult.Failed));
				throw;
			}
		}

		public void Handle(RedactionMessage.SwitchChunk message) {
			ThreadPool.QueueUserWorkItem(_ => {
				try {
					SwitchChunk(message.TargetChunkFile, message.NewChunkFile, message.Envelope);
				} catch (Exception ex) {
					Log.Error(ex, "An error has occurred when trying to switch chunk: {targetChunk} with chunk: {newChunk}.",
						message.TargetChunkFile, message.NewChunkFile);
					message.Envelope.ReplyWith(
						new RedactionMessage.SwitchChunkCompleted(SwitchChunkResult.UnexpectedError));
				}
			});
		}

		private void SwitchChunk(string targetChunkFile, string newChunkFile, IEnvelope envelope) {
			if (!IsValidSwitchChunkRequest(targetChunkFile, newChunkFile, out var newChunk, out var failReason)) {
				envelope.ReplyWith(new RedactionMessage.SwitchChunkCompleted(failReason));
				return;
			}

			_db.Manager.SwitchChunk(
				chunk: newChunk,
				verifyHash: false,
				removeChunksWithGreaterNumbers: false);

			envelope.ReplyWith(new RedactionMessage.SwitchChunkCompleted(SwitchChunkResult.Success));
		}

		private static bool IsUnsafeFileName(string fileName) {
			// protect against directory traversal attacks
			return fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains("..");
		}

		private bool IsValidSwitchChunkRequest(string targetChunkFile, string newChunkFile, out TFChunk newChunk, out SwitchChunkResult failReason) {
			newChunk = null;

			if (IsUnsafeFileName(targetChunkFile)) {
				failReason = SwitchChunkResult.TargetChunkFileNameInvalid;
				return false;
			}

			if (IsUnsafeFileName(newChunkFile)) {
				failReason = SwitchChunkResult.NewChunkFileNameInvalid;
				return false;
			}

			int targetChunkNumber;
			try {
				targetChunkNumber = _db.Config.FileNamingStrategy.GetIndexFor(targetChunkFile);
			} catch {
				failReason = SwitchChunkResult.TargetChunkFileNameInvalid;
				return false;
			}

			if (Path.GetExtension(newChunkFile) != NewChunkFileExtension) {
				failReason = SwitchChunkResult.NewChunkFileNameInvalid;
				return false;
			}

			if (!File.Exists(Path.Combine(_db.Config.Path, targetChunkFile))) {
				failReason = SwitchChunkResult.TargetChunkFileNotFound;
				return false;
			}

			var newChunkPath = Path.Combine(_db.Config.Path, newChunkFile);
			if (!File.Exists(newChunkPath)) {
				failReason = SwitchChunkResult.NewChunkFileNotFound;
				return false;
			}

			TFChunk targetChunk;
			try {
				targetChunk = _db.Manager.GetChunk(targetChunkNumber);
			} catch {
				failReason = SwitchChunkResult.TargetChunkNumberNotFound;
				return false;
			}

			if (Path.GetFileName(targetChunk.FileName) != targetChunkFile) {
				failReason = SwitchChunkResult.TargetChunkInactive;
				return false;
			}

			if (targetChunk.ChunkFooter is not { IsCompleted: true }) {
				failReason = SwitchChunkResult.TargetChunkNotCompleted;
				return false;
			}

			ChunkHeader newChunkHeader;
			ChunkFooter newChunkFooter;
			try {
				using var fs = new FileStream(newChunkPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
				newChunkHeader = ChunkHeader.FromStream(fs);
				fs.Seek(-ChunkFooter.Size, SeekOrigin.End);
				newChunkFooter = ChunkFooter.FromStream(fs);
			} catch {
				failReason = SwitchChunkResult.NewChunkHeaderOrFooterInvalid;
				return false;
			}

			if (newChunkHeader.ChunkStartNumber != targetChunk.ChunkHeader.ChunkStartNumber ||
			    newChunkHeader.ChunkEndNumber != targetChunk.ChunkHeader.ChunkEndNumber) {
				failReason = SwitchChunkResult.ChunkRangeDoesNotMatch;
				return false;
			}

			if (!newChunkFooter.IsCompleted) {
				failReason = SwitchChunkResult.NewChunkNotCompleted;
				return false;
			}

			try {
				newChunk = TFChunk.FromCompletedFile(
					filename: newChunkPath,
					verifyHash: true,
					unbufferedRead: true,
					initialReaderCount: 1,
					maxReaderCount: 1,
					optimizeReadSideCache: false,
					reduceFileCachePressure: true);
			} catch (HashValidationException) {
				failReason = SwitchChunkResult.NewChunkHashInvalid;
				return false;
			} catch {
				failReason = SwitchChunkResult.NewChunkOpenFailed;
				return false;
			} finally {
				newChunk?.Dispose();
			}

			failReason = SwitchChunkResult.None;
			return true;
		}
	}
}