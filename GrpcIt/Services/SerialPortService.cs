using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using System.Collections.Concurrent;

namespace GrpcIt.Services;

public class SerialPortService : GrpcIt.SerialPort.SerialPortBase
{
    private readonly ILogger<SerialPortService> _logger;

    public SerialPortService(ILogger<SerialPortService> logger)
    {
        _logger = logger;
    }

    public override async Task Connect(IAsyncStreamReader<SerialPortUpStream> requestStream, IServerStreamWriter<BytesValue> responseStream, ServerCallContext context)
    {
        var portSettings = await WaitForPortSettings(requestStream, context.CancellationToken);

        if (portSettings == null)
        {
            return;
        }

        using var port = new System.IO.Ports.SerialPort(portSettings.PortName, (int)portSettings.BaudRate);

        try
        {
            port.Open();
        }
        catch (Exception exp)
        {
            throw new RpcException(new Status(StatusCode.Internal, exp.Message));
        }

        async void SendFromRxToGrpcClient(object? sender, System.IO.Ports.SerialDataReceivedEventArgs args)
        {
            var buffer = new byte[port.BytesToRead];
            port.Read(buffer, 0, buffer.Length);

            await responseStream.WriteAsync(new BytesValue { Value = Google.Protobuf.ByteString.CopyFrom(buffer) });
        }

        var txQueue = new ConcurrentQueue<byte[]>();

        async void EnqueueGrpcSerialPort()
        {
            var stream = requestStream.ReadAllAsync(context.CancellationToken);

            await foreach (var req in stream)
            {
                if (req.ValueCase == SerialPortUpStream.ValueOneofCase.Data)
                {
                    txQueue.Enqueue(req.Data.ToArray());
                }
            }
        }

        async void WriteFromTxToSerialPort()
        {
            while (!context.CancellationToken.IsCancellationRequested)
            {
                if (txQueue.TryPeek(out var data))
                {
                    try
                    {
                        await port.BaseStream.WriteAsync(data);
                        txQueue.TryDequeue(out var _);
                    }
                    catch { }
                }
                else
                {
                    await Task.Delay(10);
                }
            }
        }

        port.DataReceived += SendFromRxToGrpcClient;
        context.CancellationToken.Register(() => { port.DataReceived -= SendFromRxToGrpcClient; });

        EnqueueGrpcSerialPort();
        WriteFromTxToSerialPort();

        while (!context.CancellationToken.IsCancellationRequested && port.IsOpen)
        {
            await Task.Delay(10);
        }

        port.Close();
    }

    private static async Task<SerialPortOptions?> WaitForPortSettings(IAsyncStreamReader<SerialPortUpStream> requestStream, CancellationToken cancellationToken)
    {
        var stream = requestStream.ReadAllAsync(cancellationToken);

        await foreach (var req in stream)
        {
            if (req.ValueCase == SerialPortUpStream.ValueOneofCase.Options)
            {
                return req.Options;
            }
        }

        return null;
    }
}
