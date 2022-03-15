﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using EventStore.Common.Utils;

namespace EventStore.Core.Services.RequestManager {
	public class LogNotificationService : IDisposable {
		//todo: allocate in a pool and reuse
		public struct PositionNode {
			public readonly long Position;
			public readonly TaskCompletionSource WaitTask;
			public PositionNode(long position) {
				Position = position;
				WaitTask = new TaskCompletionSource();
			}
		}
		private readonly LinkedList<PositionNode> _registeredActions = new LinkedList<PositionNode>();
		private long _logPosition;
		private long _nextLogPosition;
		private readonly Thread _thread;
		private object _registerLock = new object();
		private readonly AutoResetEvent _positionUpdated = new AutoResetEvent(true);
		private readonly ManualResetEventSlim _stopped = new ManualResetEventSlim(false);
		private readonly string _name;
		private CancellationTokenSource _cancelSource;
		private CancellationToken _canceled;
		private bool _disposedValue;

		public LogNotificationService(string name) {
			_cancelSource = new CancellationTokenSource();
			_canceled = _cancelSource.Token;
			_thread = new Thread(Notify) { IsBackground = true, Name = name };
			_thread.Start();
			_name = name;
		}
		public void UpdateLogPosition(long position) {
			if (Interlocked.Read(ref _nextLogPosition) <= position) {
				Interlocked.Exchange(ref _nextLogPosition, position);
				_positionUpdated.Set();
			}
		}
		private void Notify() {
			while (!_canceled.IsCancellationRequested) {
				Notify(Interlocked.Read(ref _nextLogPosition));
				_positionUpdated.WaitOne(TimeSpan.FromMilliseconds(50));
			}
			_stopped.Set();
		}
		private void Notify(long logPosition) {
			if (Interlocked.Read(ref _logPosition) >= logPosition) { return; }
			lock (_registerLock) {
				_logPosition = logPosition;
				var node = _registeredActions.First;
				while (node != null && node.Value.Position <= logPosition && !_canceled.IsCancellationRequested) {
					var next = node.Next;
					node.Value.WaitTask.TrySetResult();
					_registeredActions.Remove(node);
					node = next;
				}
			}			
		}

		public Task Waitfor(long position) {
			lock (_registerLock) {
				
				if (_logPosition >= position) {					
					return Task.CompletedTask;
				};
				var node = new PositionNode(position);
				if (_registeredActions.IsEmpty()|| _registeredActions.First.Value.Position >= position) {
					_registeredActions.AddFirst(node);
				} 
				else if (_registeredActions.Last.Value.Position <= position) {
					_registeredActions.AddLast(node);
				} 
				else {
					//todo: better search needed, but this should be rare
					//todo: consider adding a simple binary search over the linkedlist here with a fairly wide scan (maybe 100 nodes wide??)
					//n.b. this would need cached midpoints, or we're just scanning anyway
					var root = _registeredActions.First;
					while (root.Value.Position <= position) {
						root = root.Next;
					}
					_registeredActions.AddAfter(root, node);
				}
				return node.WaitTask.Task;
			}
		}

		protected virtual void Dispose(bool disposing) {
			if (!_disposedValue) {
				if (disposing) {
					_cancelSource.Cancel();
					_cancelSource.Dispose();
					if (_stopped.Wait(TimeSpan.FromMilliseconds(250))) {
						_registeredActions.Clear();
					}
				}
				_disposedValue = true;
			}
		}
		public void Dispose() {
			Dispose(disposing: true);
			GC.SuppressFinalize(this);
		}
	}
}