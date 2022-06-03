using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using System.Collections.Concurrent;
using System.Net.Sockets;

namespace GrpcIt.Services;

public class SocketService : GrpcIt.Socket.SocketBase
{
    private readonly ILogger<SocketService> _logger;

    public SocketService(ILogger<SocketService> logger)
    {
        _logger = logger;
    }

    public override async Task Connect(IAsyncStreamReader<SocketUpStream> requestStream, IServerStreamWriter<BytesValue> responseStream, ServerCallContext context)
    {
        var portSettings = await WaitForPortSettings(requestStream, cancellationToken: context.CancellationToken);

        if (portSettings == null)
        {
            return;
        }

        var tcpClient = new TcpClient();

        try
        {
            await tcpClient.ConnectAsync(portSettings.Address, (int)portSettings.Port, context.CancellationToken);
        }
        catch (Exception exp)
        {
            throw new RpcException(new Status(StatusCode.Internal, exp.Message));
        }

        using var stream = tcpClient.GetStream();

        async void SendFromRxToGrpcClient()
        {
            while (!context.CancellationToken.IsCancellationRequested)
            {
                if (tcpClient.Available > 0)
                {
                    var buffer = new byte[tcpClient.Available];
                    await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken: context.CancellationToken);
                    await responseStream.WriteAsync(new BytesValue { Value = Google.Protobuf.ByteString.CopyFrom(buffer) }, cancellationToken: context.CancellationToken);
                }
                else
                {
                    await Task.Delay(10, cancellationToken: context.CancellationToken);
                }
            }
        }

        var txQueue = new ConcurrentQueue<byte[]>();

        async void EnqueueToTcpClientTx()
        {
            var stream = requestStream.ReadAllAsync(context.CancellationToken);

            await foreach (var req in stream)
            {
                if (req.ValueCase == SocketUpStream.ValueOneofCase.Data)
                {
                    txQueue.Enqueue(req.Data.ToArray());
                }
            }
        }

        async void WriteFromTxQueueToTcpClient()
        {
            while (!context.CancellationToken.IsCancellationRequested)
            {
                if (txQueue.TryPeek(out var data))
                {
                    try
                    {
                        await stream.WriteAsync(data, cancellationToken: context.CancellationToken);
                        await stream.FlushAsync(cancellationToken: context.CancellationToken);
                        txQueue.TryDequeue(out var _);
                    }
                    catch { }
                }
                else
                {
                    await Task.Delay(10, cancellationToken: context.CancellationToken);
                }
            }
        }

        SendFromRxToGrpcClient();
        EnqueueToTcpClientTx();
        WriteFromTxQueueToTcpClient();

        while (!context.CancellationToken.IsCancellationRequested && tcpClient.Connected)
        {
            await Task.Delay(10, cancellationToken: context.CancellationToken);
        }

        tcpClient.Close();
    }

    private static async Task<SocketOptions?> WaitForPortSettings(IAsyncStreamReader<SocketUpStream> requestStream, CancellationToken cancellationToken)
    {
        var stream = requestStream.ReadAllAsync(cancellationToken);

        await foreach (var req in stream)
        {
            if (req.ValueCase == SocketUpStream.ValueOneofCase.Options)
            {
                return req.Options;
            }
        }

        return null;
    }
}
