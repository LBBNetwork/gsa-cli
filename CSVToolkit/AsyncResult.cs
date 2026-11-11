using System;
using System.Threading;

namespace C3.CSVToolkit
{
    /// <summary>Base operation handle (mirrors EalItemr.AsyncResult, CSV-9).
    /// Each operation runs on its own background thread; the handle carries
    /// completion state. Whitelist-only primitives: Thread + Join.</summary>
    public abstract class AsyncResult
    {
        protected object _gate = new object();
        protected Exception _error;
        protected bool _completed;
        private Thread _thread;

        /// <summary>True once the worker thread has finished (success or failure).</summary>
        public bool IsCompleted
        {
            get { lock (_gate) { return _completed; } }
        }

        /// <summary>Worker exception, or null when the operation succeeded.</summary>
        public Exception Error
        {
            get { lock (_gate) { return _error; } }
        }

        /// <summary>Managed thread id of this operation's own worker thread.</summary>
        public long WorkerThreadId
        {
            get { return _thread != null ? _thread.ManagedThreadId : 0; }
        }

        /// <summary>Block the calling thread until the operation finishes.</summary>
        public void Wait()
        {
            if (_thread != null) _thread.Join();
        }

        internal void Start(string opName, ThreadStart work)
        {
            _thread = new Thread(work);
            _thread.Name = "csvtoolkit-" + opName;
            _thread.IsBackground = true;
            _thread.Start();
        }

        protected void Finish(Exception error)
        {
            lock (_gate) { _error = error; _completed = true; }
        }
    }

    /// <summary>Typed operation handle: Wait() then read Result, or use Completed.</summary>
    public sealed class AsyncResult<T> : AsyncResult
    {
        private T _result;

        /// <summary>Raised on the worker thread when the operation completes.</summary>
        public event EventHandler<AsyncCompletedEventArgs<T>> Completed;

        /// <summary>Result value. Blocks until the operation completes, then
        /// returns the result or throws the worker exception on failure.
        /// For non-blocking observation use IsCompleted / Wait / Completed.</summary>
        public T Result
        {
            get
            {
                if (!IsCompleted) Wait();
                lock (_gate)
                {
                    if (_error != null) throw _error;
                    return _result;
                }
            }
        }

        internal void SetResult(T result, Exception error)
        {
            _result = result;
            Finish(error);
        }

        internal void RaiseCompleted(T result, Exception error)
        {
            EventHandler<AsyncCompletedEventArgs<T>> h = Completed;
            if (h != null)
                h(this, new AsyncCompletedEventArgs<T>(result, error));
        }
    }

    /// <summary>EventArgs for AsyncResult.Completed (WebClient-style, .NET 2.0).</summary>
    public sealed class AsyncCompletedEventArgs<T> : EventArgs
    {
        private readonly T _result;
        private readonly Exception _error;

        internal AsyncCompletedEventArgs(T result, Exception error)
        {
            _result = result;
            _error = error;
        }

        /// <summary>Operation result (default(T) when the operation failed).</summary>
        public T Result { get { return _result; } }

        /// <summary>Worker exception, or null on success.</summary>
        public Exception Error { get { return _error; } }
    }
}
