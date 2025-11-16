using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Serilog.Sinks.Network.Test
{
    internal static class ServerPoller
    {
        public static async Task<string> PollForReceivedData(Socket socket, bool udp = false)
        {
            var buffer = new byte[1000];
            var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromSeconds(30.0));
            var result = new List<byte>();

            Socket clientSocket;
            try
            {
                if (udp)
                {
                    clientSocket = socket;
                }
                else
                {
                    //clientSocket = await socket.AcceptAsync(cts.Token);
                    // AcceptAsync with CancellationToken is NOT supported in .NET Framework
                    // Use Task-based wrapper with cancellation
                    clientSocket = await AcceptAsyncWithCancellation(socket, cts.Token);
                }

                var isDone = false;
                while (!isDone && !cts.Token.IsCancellationRequested)
                {
                    int bytesRead = await ReceiveAsyncWithCancellation(clientSocket, buffer, SocketFlags.None, cts.Token);

                    if (bytesRead <= 0)
                    {
                        isDone = true;
                        break;
                    }

                    for (int i = 0; i < bytesRead; i++)
                    {
                        result.Add(buffer[i]);
                    }

                    if (bytesRead < buffer.Length)
                    {
                        isDone = true;
                    }
                }

                if (cts.Token.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cts.Token);
                }

                return Encoding.ASCII.GetString(result.ToArray());
            }
            catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
            {
                throw; // Re-throw timeout/cancellation
            }
            finally
            {
                cts.Dispose();
            }
        }

        private static Task<Socket> AcceptAsyncWithCancellation(Socket socket, CancellationToken token)
        {
            var tcs = new TaskCompletionSource<Socket>();

            token.Register(() =>
            {
                tcs.TrySetCanceled(token);
            });

            socket.BeginAccept(ar =>
            {
                try
                {
                    var client = socket.EndAccept(ar);
                    tcs.TrySetResult(client);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }, null);

            return tcs.Task;
        }

        private static Task<int> ReceiveAsyncWithCancellation(Socket socket, byte[] buffer, SocketFlags flags, CancellationToken token)
        {
            var tcs = new TaskCompletionSource<int>();
            var args = new SocketAsyncEventArgs();
            args.SetBuffer(buffer, 0, buffer.Length);
            args.SocketFlags = flags;

            var registration = token.Register(() =>
            {
                tcs.TrySetCanceled(token);
                // Attempt to cancel pending operation
                try { socket.Shutdown(SocketShutdown.Receive); } catch { }
            });

            args.Completed += (s, e) =>
            {
                registration.Dispose();
                if (e.SocketError == SocketError.Success)
                {
                    tcs.TrySetResult(e.BytesTransferred);
                }
                else
                {
                    tcs.TrySetException(new SocketException((int)e.SocketError));
                }
            };

            bool willRaiseEvent = socket.ReceiveAsync(args);
            if (!willRaiseEvent)
            {
                // Operation completed synchronously
                registration.Dispose();
                if (args.SocketError == SocketError.Success)
                {
                    tcs.TrySetResult(args.BytesTransferred);
                }
                else
                {
                    tcs.TrySetException(new SocketException((int)args.SocketError));
                }
            }

            return tcs.Task;
        }
    }
}