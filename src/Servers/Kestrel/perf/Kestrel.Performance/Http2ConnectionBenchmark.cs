// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http2;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Infrastructure;
using Microsoft.AspNetCore.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Microsoft.AspNetCore.Server.Kestrel.Performance
{
    public class Http2ConnectionBenchmark
    {
        private MemoryPool<byte> _memoryPool;
        private HttpRequestHeaders _httpRequestHeaders;
        private Http2Connection _connection;
        private Http2HeadersEnumerator _requestHeadersEnumerator;
        private int _currentStreamId;
        private byte[] _headersBuffer;
        private DuplexPipe.DuplexPipePair _connectionPair;
        private Http2Frame _httpFrame;
        private byte[] _requestData;
        private byte[] _responseData;
        private int _dataSendWindow;

        // TODO: There is a bug in how WINDOW_UPDATE is being processed.
        // Setting this to a large value will cause benchmark to hang.
        [Params(0, 16384)]
        public int RequestDataLength { get; set; }

        [Params(0)]
        public int ResponseDataLength { get; set; }

        private bool HasRequestData => RequestDataLength > 0;

        [GlobalSetup]
        public void GlobalSetup()
        {
            _memoryPool = SlabMemoryPoolFactory.Create();
            _httpFrame = new Http2Frame();
            _responseData = new byte[ResponseDataLength];
            _requestData = new byte[RequestDataLength];

            var options = new PipeOptions(_memoryPool, readerScheduler: PipeScheduler.Inline, writerScheduler: PipeScheduler.Inline, useSynchronizationContext: false);

            _connectionPair = DuplexPipe.CreateConnectionPair(options, options);

            _httpRequestHeaders = new HttpRequestHeaders();
            _httpRequestHeaders.Append(HeaderNames.Method, new StringValues("GET"));
            _httpRequestHeaders.Append(HeaderNames.Path, new StringValues("/"));
            _httpRequestHeaders.Append(HeaderNames.Scheme, new StringValues("http"));
            _httpRequestHeaders.Append(HeaderNames.Authority, new StringValues("localhost:80"));

            _headersBuffer = new byte[1024 * 16];

            var serviceContext = new ServiceContext
            {
                DateHeaderValueManager = new DateHeaderValueManager(),
                ServerOptions = new KestrelServerOptions(),
                Log = new KestrelTrace(NullLogger.Instance),
                SystemClock = new MockSystemClock()
            };
            serviceContext.DateHeaderValueManager.OnHeartbeat(default);

            _connection = new Http2Connection(new HttpConnectionContext
            {
                MemoryPool = _memoryPool,
                ConnectionId = "TestConnectionId",
                Protocols = HttpProtocols.Http2,
                Transport = _connectionPair.Transport,
                ServiceContext = serviceContext,
                ConnectionFeatures = new FeatureCollection(),
                TimeoutControl = new MockTimeoutControl(),
            });

            _requestHeadersEnumerator = new Http2HeadersEnumerator();

            _currentStreamId = 1;

            _ = _connection.ProcessRequestsAsync(new DummyApplication(async c =>
            {
                if (HasRequestData)
                {
                    ReadResult result;
                    do
                    {
                        result = await c.Request.BodyReader.ReadAsync();
                        c.Request.BodyReader.AdvanceTo(result.Buffer.End);
                    }
                    while (!result.IsCompleted);
                }

                if (ResponseDataLength > 0)
                {
                    await c.Response.BodyWriter.WriteAsync(_responseData);
                }
            }, new MockHttpContextFactory()));

            _connectionPair.Application.Output.Write(Http2Connection.ClientPreface);
            _connectionPair.Application.Output.WriteSettings(new Http2PeerSettings());
            _connectionPair.Application.Output.FlushAsync().GetAwaiter().GetResult();

            // Read past connection setup frames
            ReceiveFrameAsync(_connectionPair.Application.Input, _httpFrame).GetAwaiter().GetResult();
            Debug.Assert(_httpFrame.Type == Http2FrameType.SETTINGS);
            ReceiveFrameAsync(_connectionPair.Application.Input, _httpFrame).GetAwaiter().GetResult();
            Debug.Assert(_httpFrame.Type == Http2FrameType.WINDOW_UPDATE);
            ReceiveFrameAsync(_connectionPair.Application.Input, _httpFrame).GetAwaiter().GetResult();
            Debug.Assert(_httpFrame.Type == Http2FrameType.SETTINGS);
        }

        private async Task ReadResponse()
        {
            _dataSendWindow = (int)Http2PeerSettings.DefaultInitialWindowSize;

            var dataRead = 0;
            while (true)
            {
                await ReceiveFrameAsync(_connectionPair.Application.Input, _httpFrame);

                if (_httpFrame.StreamId != _currentStreamId && _httpFrame.StreamId != 0)
                {
                    throw new Exception($"Unexpected stream ID: {_httpFrame.StreamId}");
                }

                if (_httpFrame.Type == Http2FrameType.DATA)
                {
                    dataRead += _httpFrame.DataPayloadLength;
                }

                if (_httpFrame.Type == Http2FrameType.WINDOW_UPDATE)
                {
                    if (_httpFrame.StreamId == _currentStreamId)
                    {
                        Interlocked.Add(ref _dataSendWindow, _httpFrame.WindowUpdateSizeIncrement);
                    }
                }

                if (dataRead > 1024 * 32)
                {
                    _connectionPair.Application.Output.WriteWindowUpdateAsync(streamId: 0, dataRead, _httpFrame);
                    await _connectionPair.Application.Output.FlushAsync();

                    dataRead = 0;
                }

                if ((_httpFrame.HeadersFlags & Http2HeadersFrameFlags.END_STREAM) == Http2HeadersFrameFlags.END_STREAM)
                {
                    break;
                }
            }
        }

        [Benchmark]
        public async Task EmptyRequest()
        {
            var responseTask = ReadResponse();

            _requestHeadersEnumerator.Initialize(_httpRequestHeaders);
            _requestHeadersEnumerator.MoveNext();

            _connectionPair.Application.Output.WriteStartStream(streamId: _currentStreamId, _requestHeadersEnumerator, _headersBuffer, endStream: !HasRequestData, frame: _httpFrame);
            if (HasRequestData)
            {
                var remainingData = _requestData.AsMemory();
                while (remainingData.Length > 0)
                {
                    var length = Math.Min(Math.Min(remainingData.Length, (int)Http2PeerSettings.DefaultMaxFrameSize), _dataSendWindow);
                    if (length > 0)
                    {
                        var endStream = remainingData.Length == length;
                        _connectionPair.Application.Output.WriteData(streamId: _currentStreamId, _requestData.AsMemory(0, length), endStream, _httpFrame);
                        remainingData = remainingData.Slice(length);

                        Interlocked.Add(ref _dataSendWindow, -length);

                        await _connectionPair.Application.Output.FlushAsync();
                    }
                }
            }
            else
            {
                await _connectionPair.Application.Output.FlushAsync();
            }

            await responseTask;

            _currentStreamId += 2;
        }

        internal async ValueTask ReceiveFrameAsync(PipeReader pipeReader, Http2Frame frame, uint maxFrameSize = Http2PeerSettings.DefaultMaxFrameSize)
        {
            while (true)
            {
                var result = await pipeReader.ReadAsync();
                var buffer = result.Buffer;
                var consumed = buffer.Start;
                var examined = buffer.Start;

                try
                {
                    if (Http2FrameReader.TryReadFrame(ref buffer, frame, maxFrameSize, out var framePayload))
                    {
                        consumed = examined = framePayload.End;
                        return;
                    }
                    else
                    {
                        examined = buffer.End;
                    }

                    if (result.IsCompleted)
                    {
                        throw new IOException("The reader completed without returning a frame.");
                    }
                }
                finally
                {
                    pipeReader.AdvanceTo(consumed, examined);
                }
            }
        }

        [GlobalCleanup]
        public void Dispose()
        {
            _connectionPair.Application.Output.Complete();
            _memoryPool?.Dispose();
        }
    }
}
