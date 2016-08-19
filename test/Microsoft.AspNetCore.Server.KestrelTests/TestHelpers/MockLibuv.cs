// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Server.Kestrel.Internal.Networking;

namespace Microsoft.AspNetCore.Server.KestrelTests.TestHelpers
{
    public class MockLibuv : Libuv
    {
        private UvAsyncHandle _postHandle;
        private uv_async_cb _onPost;

        private readonly object _postLock = new object();
        private TaskCompletionSource<object> _onPostTcs = new TaskCompletionSource<object>();
        private bool _completedOnPostTcs;
        private bool _sendCalled;

        private bool _stopLoop;
        private readonly ManualResetEventSlim _loopWh = new ManualResetEventSlim();

        private readonly string _stackTrace;

        unsafe public MockLibuv()
            : base(onlyForTesting: true)
        {
            _stackTrace = Environment.StackTrace;

            OnWrite = (socket, buffers, triggerCompleted) =>
            {
                triggerCompleted(0);
                return 0;
            };

            _uv_write = UvWrite;

            _uv_async_send = postHandle =>
            {
                lock (_postLock)
                {
                    if (_completedOnPostTcs)
                    {
                        _onPostTcs = new TaskCompletionSource<object>();
                        _completedOnPostTcs = false;
                    }

                    PostCount++;

                    _sendCalled = true;
                    _loopWh.Set();
                }

                return 0;
            };

            _uv_async_init = (loop, postHandle, callback) =>
            {
                _postHandle = postHandle;
                _onPost = callback;

                return 0;
            };

            _uv_run = (loopHandle, mode) =>
            {
                while (!_stopLoop)
                {
                    _loopWh.Wait();
                    KestrelThreadBlocker.Wait();
                    TaskCompletionSource<object> onPostTcs = null;

                    lock (_postLock)
                    {
                        _sendCalled = false;
                        _loopWh.Reset();
                        _onPost(_postHandle.InternalGetHandle());

                        // Allow the loop to be run again before completing
                        // _onPostTcs given a nested uv_async_send call.
                        if (!_sendCalled)
                        {
                            // Ensure any subsequent calls to uv_async_send
                            // create a new _onPostTcs to be completed.
                            _completedOnPostTcs = true;
                            onPostTcs = _onPostTcs;
                        }
                    }

                    // Calling TrySetResult outside the lock to avoid deadlock
                    // when the code attempts to call uv_async_send after awaiting
                    // OnPostTask.
                    onPostTcs?.TrySetResult(null);
                }

                return 0;
            };

            _uv_ref = handle => { };
            _uv_unref = handle =>
            {
                _stopLoop = true;
                _loopWh.Set();
            };

            _uv_stop = handle =>
            {
                _stopLoop = true;
                _loopWh.Set();
            };

            _uv_req_size = reqType => IntPtr.Size;
            _uv_loop_size = () => IntPtr.Size;
            _uv_handle_size = handleType => IntPtr.Size;
            _uv_loop_init = loop => 0;
            _uv_tcp_init = (loopHandle, tcpHandle) => 0;
            _uv_close = (handle, callback) => callback(handle);
            _uv_loop_close = handle => 0;
            _uv_walk = (loop, callback, ignore) => 0;
            _uv_err_name = errno => IntPtr.Zero;
            _uv_strerror = errno => IntPtr.Zero;
            _uv_read_start = UvReadStart;
            _uv_read_stop = handle => 0;
            _uv_unsafe_async_send = handle =>
            {
                throw new Exception($"Why is this getting called?{Environment.NewLine}{_stackTrace}");
            };

            _uv_timer_init = (loop, handle) => 0;
            _uv_timer_start = (handle, callback, timeout, repeat) => 0;
            _uv_timer_stop = handle => 0;
        }

        public Func<UvStreamHandle, int, Action<int>, int> OnWrite { get; set; }

        public uv_alloc_cb AllocCallback { get; set; }

        public uv_read_cb ReadCallback { get; set; }

        public int PostCount { get; set; }

        public Task OnPostTask => _onPostTcs.Task;

        public ManualResetEventSlim KestrelThreadBlocker { get; } = new ManualResetEventSlim(true);

        private int UvReadStart(UvStreamHandle handle, uv_alloc_cb allocCallback, uv_read_cb readCallback)
        {
            AllocCallback = allocCallback;
            ReadCallback = readCallback;
            return 0;
        }

        unsafe private int UvWrite(UvRequest req, UvStreamHandle handle, uv_buf_t* bufs, int nbufs, uv_write_cb cb)
        {
            return OnWrite(handle, nbufs, status => cb(req.InternalGetHandle(), status));
        }
    }
}
