// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.AspNetCore.SignalR.Tests;
using Microsoft.AspNetCore.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Xunit;

namespace Microsoft.AspNetCore.SignalR.Client.FunctionalTests;

public class HubConnectionTestsCollection : ICollectionFixture<InProcessTestServer<Startup>>
{
    public const string Name = nameof(HubConnectionTestsCollection);
}

[Collection(HubConnectionTestsCollection.Name)]
public class HubConnectionTests : FunctionalTestBase
{
    private const string DefaultHubDispatcherLoggerName = "Microsoft.AspNetCore.SignalR.Internal.DefaultHubDispatcher";

    private HubConnection CreateHubConnection(
        string url,
        string path = null,
        HttpTransportType? transportType = null,
        IHubProtocol protocol = null,
        ILoggerFactory loggerFactory = null,
        bool withAutomaticReconnect = false)
    {
        var hubConnectionBuilder = new HubConnectionBuilder();

        hubConnectionBuilder.WithUrl(url + path);

        protocol ??= new JsonHubProtocol();
        hubConnectionBuilder.Services.AddSingleton(protocol);

        if (loggerFactory != null)
        {
            hubConnectionBuilder.WithLoggerFactory(loggerFactory);
        }

        if (withAutomaticReconnect)
        {
            hubConnectionBuilder.WithAutomaticReconnect();
        }

        transportType ??= HttpTransportType.LongPolling | HttpTransportType.WebSockets | HttpTransportType.ServerSentEvents;

        var delegateConnectionFactory = new DelegateConnectionFactory(
            GetHttpConnectionFactory(url, loggerFactory, path, transportType.Value, protocol.TransferFormat));
        hubConnectionBuilder.Services.AddSingleton<IConnectionFactory>(delegateConnectionFactory);

        return hubConnectionBuilder.Build();
    }

    private Func<EndPoint, ValueTask<ConnectionContext>> GetHttpConnectionFactory(string url, ILoggerFactory loggerFactory, string path, HttpTransportType transportType, TransferFormat transferFormat)
    {
        return async endPoint =>
        {
            var httpEndpoint = (UriEndPoint)endPoint;
            var options = new HttpConnectionOptions { Url = httpEndpoint.Uri, Transports = transportType, DefaultTransferFormat = transferFormat };
            var connection = new HttpConnection(options, loggerFactory);

            await connection.StartAsync();

            return connection;
        };
    }

    [Theory]
    [MemberData(nameof(HubProtocolsAndTransportsAndHubPaths))]
    public async Task CheckFixedMessage(string protocolName, HttpTransportType transportType, string path)
    {
        var protocol = HubProtocols[protocolName];
        await using (var server = await StartServer<Startup>())
        {
            var connectionBuilder = new HubConnectionBuilder()
                .WithLoggerFactory(LoggerFactory)
                .WithUrl(server.Url + path, transportType);
            connectionBuilder.Services.AddSingleton(protocol);

            var connection = connectionBuilder.Build();

            try
            {
                await connection.StartAsync().DefaultTimeout();

                var result = await connection.InvokeAsync<string>(nameof(TestHub.HelloWorld)).DefaultTimeout();

                Assert.Equal("Hello World!", result);
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await connection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Fact]
    public async Task ServerRejectsClientWithOldProtocol()
    {
        bool ExpectedError(WriteContext writeContext)
        {
            return writeContext.LoggerName == typeof(HttpConnection).FullName &&
                writeContext.EventId.Name == "ErrorWithNegotiation";
        }

        var protocol = HubProtocols["json"];
        await using (var server = await StartServer<Startup>(ExpectedError))
        {
            var connectionBuilder = new HubConnectionBuilder()
                .WithLoggerFactory(LoggerFactory)
                .WithUrl(server.Url + "/negotiateProtocolVersion12", HttpTransportType.LongPolling);
            connectionBuilder.Services.AddSingleton(protocol);

            var connection = connectionBuilder.Build();

            try
            {
                var ex = await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync()).DefaultTimeout();
                Assert.Equal("The client requested version '1', but the server does not support this version.", ex.Message);
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await connection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Fact]
    public async Task ClientCanConnectToServerWithLowerMinimumProtocol()
    {
        var protocol = HubProtocols["json"];
        await using (var server = await StartServer<Startup>())
        {
            var connectionBuilder = new HubConnectionBuilder()
                .WithLoggerFactory(LoggerFactory)
                .WithUrl(server.Url + "/negotiateProtocolVersionNegative", HttpTransportType.LongPolling);
            connectionBuilder.Services.AddSingleton(protocol);

            var connection = connectionBuilder.Build();

            try
            {
                await connection.StartAsync().DefaultTimeout();
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await connection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Theory]
    [MemberData(nameof(HubProtocolsAndTransportsAndHubPaths))]
    public async Task CanSendAndReceiveMessage(string protocolName, HttpTransportType transportType, string path)
    {
        var protocol = HubProtocols[protocolName];
        await using (var server = await StartServer<Startup>())
        {
            const string originalMessage = "SignalR";
            var connection = CreateHubConnection(server.Url, path, transportType, protocol, LoggerFactory);
            try
            {
                await connection.StartAsync().DefaultTimeout();

                var result = await connection.InvokeAsync<string>(nameof(TestHub.Echo), originalMessage).DefaultTimeout();

                Assert.Equal(originalMessage, result);
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await connection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Theory]
    [MemberData(nameof(HubProtocolsList))]
    public async Task CanSendNull(string protocolName)
    {
        var protocol = HubProtocols[protocolName];
        await using (var server = await StartServer<Startup>())
        {
            var connection = CreateHubConnection(server.Url, "/default", HttpTransportType.LongPolling, protocol, LoggerFactory);
            try
            {
                await connection.StartAsync().DefaultTimeout();

                var result = await connection.InvokeAsync<string>(nameof(TestHub.Echo), null).DefaultTimeout();

                Assert.Null(result);
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await connection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Theory]
    [MemberData(nameof(HubProtocolsAndTransportsAndHubPaths))]
    [LogLevel(LogLevel.Trace)]
    public async Task CanStopAndStartConnection(string protocolName, HttpTransportType transportType, string path)
    {
        var protocol = HubProtocols[protocolName];
        await using (var server = await StartServer<Startup>())
        {
            const string originalMessage = "SignalR";
            var connection = CreateHubConnection(server.Url, path, transportType, protocol, LoggerFactory);
            try
            {
                await connection.StartAsync().DefaultTimeout();
                var result = await connection.InvokeAsync<string>(nameof(TestHub.Echo), originalMessage).DefaultTimeout();
                Assert.Equal(originalMessage, result);
                await connection.StopAsync().DefaultTimeout();
                await connection.StartAsync().DefaultTimeout();
                result = await connection.InvokeAsync<string>(nameof(TestHub.Echo), originalMessage).DefaultTimeout();
                Assert.Equal(originalMessage, result);
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await connection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Theory]
    [MemberData(nameof(HubProtocolsAndTransportsAndHubPaths))]
    [LogLevel(LogLevel.Trace)]
    public async Task CanAccessConnectionIdFromHubConnection(string protocolName, HttpTransportType transportType, string path)
    {
        var protocol = HubProtocols[protocolName];
        await using (var server = await StartServer<Startup>())
        {
            var connection = CreateHubConnection(server.Url, path, transportType, protocol, LoggerFactory);
            try
            {
                Assert.Null(connection.ConnectionId);
                await connection.StartAsync().DefaultTimeout();
                var originalClientConnectionId = connection.ConnectionId;
                var connectionIdFromServer = await connection.InvokeAsync<string>(nameof(TestHub.GetCallerConnectionId)).DefaultTimeout();
                Assert.Equal(connection.ConnectionId, connectionIdFromServer);
                await connection.StopAsync().DefaultTimeout();
                Assert.Null(connection.ConnectionId);
                await connection.StartAsync().DefaultTimeout();
                connectionIdFromServer = await connection.InvokeAsync<string>(nameof(TestHub.GetCallerConnectionId)).DefaultTimeout();
                Assert.NotEqual(originalClientConnectionId, connectionIdFromServer);
                Assert.Equal(connection.ConnectionId, connectionIdFromServer);
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await connection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Theory]
    [MemberData(nameof(HubProtocolsAndTransportsAndHubPaths))]
    [LogLevel(LogLevel.Trace)]
    public async Task CanStartConnectionFromClosedEvent(string protocolName, HttpTransportType transportType, string path)
    {
        var protocol = HubProtocols[protocolName];
        await using (var server = await StartServer<Startup>())
        {
            var logger = LoggerFactory.CreateLogger<HubConnectionTests>();
            const string originalMessage = "SignalR";

            var connection = CreateHubConnection(server.Url, path, transportType, protocol, LoggerFactory);
            var restartTcs = new TaskCompletionSource();
            connection.Closed += async e =>
            {
                try
                {
                    logger.LogInformation("Closed event triggered");
                    if (!restartTcs.Task.IsCompleted)
                    {
                        logger.LogInformation("Restarting connection");
                        await connection.StartAsync().DefaultTimeout();
                        logger.LogInformation("Restarted connection");
                        restartTcs.SetResult();
                    }
                }
                catch (Exception ex)
                {
                        // It's important to try catch here since this happens
                        // on a thread pool thread
                        restartTcs.TrySetException(ex);
                }
            };

            try
            {
                await connection.StartAsync().DefaultTimeout();
                var result = await connection.InvokeAsync<string>(nameof(TestHub.Echo), originalMessage).DefaultTimeout();
                Assert.Equal(originalMessage, result);

                logger.LogInformation("Stopping connection");
                await connection.StopAsync().DefaultTimeout();

                logger.LogInformation("Waiting for reconnect");
                await restartTcs.Task.DefaultTimeout();
                logger.LogInformation("Reconnection complete");

                result = await connection.InvokeAsync<string>(nameof(TestHub.Echo), originalMessage).DefaultTimeout();
                Assert.Equal(originalMessage, result);

            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await connection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Theory]
    [MemberData(nameof(HubProtocolsAndTransportsAndHubPaths))]
    public async Task MethodsAreCaseInsensitive(string protocolName, HttpTransportType transportType, string path)
    {
        var protocol = HubProtocols[protocolName];
        await using (var server = await StartServer<Startup>())
        {
            const string originalMessage = "SignalR";
            var connection = CreateHubConnection(server.Url, path, transportType, protocol, LoggerFactory);
            try
            {
                await connection.StartAsync().DefaultTimeout();

                var result = await connection.InvokeAsync<string>(nameof(TestHub.Echo).ToLowerInvariant(), originalMessage).DefaultTimeout();

                Assert.Equal(originalMessage, result);
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await connection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Theory]
    [MemberData(nameof(HubProtocolsAndTransportsAndHubPaths))]
    [LogLevel(LogLevel.Trace)]
    public async Task CanInvokeFromOnHandler(string protocolName, HttpTransportType transportType, string path)
    {
        var protocol = HubProtocols[protocolName];
        await using (var server = await StartServer<Startup>())
        {
            const string originalMessage = "SignalR";

            var connection = CreateHubConnection(server.Url, path, transportType, protocol, LoggerFactory);
            try
            {
                await connection.StartAsync().DefaultTimeout();

                var helloWorldTcs = new TaskCompletionSource<string>();
                var echoTcs = new TaskCompletionSource<string>();
                connection.On<string>("Echo", async (message) =>
                {
                    echoTcs.SetResult(message);
                    helloWorldTcs.SetResult(await connection.InvokeAsync<string>(nameof(TestHub.HelloWorld)).DefaultTimeout());
                });

                await connection.InvokeAsync("CallEcho", originalMessage).DefaultTimeout();

                Assert.Equal(originalMessage, await echoTcs.Task.DefaultTimeout());
                Assert.Equal("Hello World!", await helloWorldTcs.Task.DefaultTimeout());
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await connection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Theory]
    [MemberData(nameof(HubProtocolsAndTransportsAndHubPaths))]
    [LogLevel(LogLevel.Trace)]
    public async Task StreamAsyncCoreTest(string protocolName, HttpTransportType transportType, string path)
    {
        var protocol = HubProtocols[protocolName];
        await using (var server = await StartServer<Startup>())
        {
            var connection = CreateHubConnection(server.Url, path, transportType, protocol, LoggerFactory);
            try
            {
                await connection.StartAsync().DefaultTimeout();
                var expectedValue = 0;
                var streamTo = 5;
                var asyncEnumerable = connection.StreamAsyncCore<int>("Stream", new object[] { streamTo });
                await foreach (var streamValue in asyncEnumerable)
                {
                    Assert.Equal(expectedValue, streamValue);
                    expectedValue++;
                }

                Assert.Equal(streamTo, expectedValue);
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await connection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Theory]
    [InlineData("json")]
    [InlineData("messagepack")]
    public async Task CanStreamToHubWithIAsyncEnumerableMethodArg(string protocolName)
    {
        var protocol = HubProtocols[protocolName];
        await using (var server = await StartServer<Startup>())
        {
            var connection = CreateHubConnection(server.Url, "/default", HttpTransportType.WebSockets, protocol, LoggerFactory);
            try
            {
                async IAsyncEnumerable<int> ClientStreamData(int value)
                {
                    for (var i = 0; i < value; i++)
                    {
                        yield return i;
                        await Task.Delay(10);
                    }
                }

                var streamTo = 5;
                var stream = ClientStreamData(streamTo);

                await connection.StartAsync().DefaultTimeout();
                var expectedValue = 0;
                var asyncEnumerable = connection.StreamAsync<int>("StreamIAsyncConsumer", stream);
                await foreach (var streamValue in asyncEnumerable)
                {
                    Assert.Equal(expectedValue, streamValue);
                    expectedValue++;
                }

                Assert.Equal(streamTo, expectedValue);
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await connection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Theory]
    [MemberData(nameof(HubProtocolsAndTransportsAndHubPaths))]
    [LogLevel(LogLevel.Trace)]
    public async Task StreamAsyncTest(string protocolName, HttpTransportType transportType, string path)
    {
        var protocol = HubProtocols[protocolName];
        await using (var server = await StartServer<Startup>())
        {
            var connection = CreateHubConnection(server.Url, path, transportType, protocol, LoggerFactory);
            try
            {
                await connection.StartAsync().DefaultTimeout();
                var expectedValue = 0;
                var streamTo = 5;
                var asyncEnumerable = connection.StreamAsync<int>("Stream", streamTo);
                await foreach (var streamValue in asyncEnumerable)
                {
                    Assert.Equal(expectedValue, streamValue);
                    expectedValue++;
                }

                Assert.Equal(streamTo, expectedValue);
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await connection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Theory]
    [MemberData(nameof(HubProtocolsAndTransportsAndHubPaths))]
    [LogLevel(LogLevel.Trace)]
    public async Task StreamAsyncDoesNotStartIfTokenAlreadyCanceled(string protocolName, HttpTransportType transportType, string path)
    {
        var protocol = HubProtocols[protocolName];
        await using (var server = await StartServer<Startup>())
        {
            var connection = CreateHubConnection(server.Url, path, transportType, protocol, LoggerFactory);
            try
            {
                await connection.StartAsync().DefaultTimeout();

                var cts = new CancellationTokenSource();
                cts.Cancel();

                var ex = Assert.ThrowsAsync<OperationCanceledException>(async () =>
                {
                    var stream = connection.StreamAsync<int>("Stream", 5, cts.Token);
                    await foreach (var streamValue in stream)
                    {
                        Assert.True(false, "Expected an exception from the streaming invocation.");
                    }
                });
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await connection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Theory]
    [MemberData(nameof(HubProtocolsAndTransportsAndHubPaths))]
    [LogLevel(LogLevel.Trace)]
    public async Task StreamAsyncCanBeCanceled(string protocolName, HttpTransportType transportType, string path)
    {
        var protocol = HubProtocols[protocolName];
        await using (var server = await StartServer<Startup>())
        {
            var connection = CreateHubConnection(server.Url, path, transportType, protocol, LoggerFactory);
            try
            {
                await connection.StartAsync().DefaultTimeout();

                var cts = new CancellationTokenSource();

                var stream = connection.StreamAsync<int>("Stream", 1000, cts.Token);
                var results = new List<int>();

                var enumerator = stream.GetAsyncEnumerator();
                await Assert.ThrowsAsync<TaskCanceledException>(async () =>
                {
                    while (await enumerator.MoveNextAsync())
                    {
                        results.Add(enumerator.Current);
                        cts.Cancel();
                    }
                });

                Assert.True(results.Count > 0 && results.Count < 1000);
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await connection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Theory]
    [MemberData(nameof(HubProtocolsAndTransportsAndHubPaths))]
    [LogLevel(LogLevel.Trace)]
    public async Task StreamAsyncWithException(string protocolName, HttpTransportType transportType, string path)
    {
        bool ExpectedErrors(WriteContext writeContext)
        {
            return writeContext.LoggerName == DefaultHubDispatcherLoggerName &&
                   writeContext.EventId.Name == "FailedInvokingHubMethod";
        }

        var protocol = HubProtocols[protocolName];
        await using (var server = await StartServer<Startup>(ExpectedErrors))
        {
            var connection = CreateHubConnection(server.Url, path, transportType, protocol, LoggerFactory);
            try
            {
                await connection.StartAsync().DefaultTimeout();
                var asyncEnumerable = connection.StreamAsync<int>("StreamException");
                var ex = await Assert.ThrowsAsync<HubException>(async () =>
                {
                    await foreach (var streamValue in asyncEnumerable)
                    {
                        Assert.True(false, "Expected an exception from the streaming invocation.");
                    }
                });

                Assert.Equal("An unexpected error occurred invoking 'StreamException' on the server. InvalidOperationException: Error occurred while streaming.", ex.Message);

            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await connection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Theory]
    [MemberData(nameof(HubProtocolsAndTransportsAndHubPaths))]
    [LogLevel(LogLevel.Trace)]
    public async Task CanInvokeClientMethodFromServer(string protocolName, HttpTransportType transportType, string path)
    {
        var protocol = HubProtocols[protocolName];
        await using (var server = await StartServer<Startup>())
        {
            const string originalMessage = "SignalR";

            var connection = CreateHubConnection(server.Url, path, transportType, protocol, LoggerFactory);
            try
            {
                await connection.StartAsync().DefaultTimeout();

                var tcs = new TaskCompletionSource<string>();
                connection.On<string>("Echo", tcs.SetResult);

                await connection.InvokeAsync("CallEcho", originalMessage).DefaultTimeout();

                Assert.Equal(originalMessage, await tcs.Task.DefaultTimeout());
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await connection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Theory]
    [MemberData(nameof(HubProtocolsAndTransportsAndHubPaths))]
    [LogLevel(LogLevel.Trace)]
    public async Task InvokeNonExistantClientMethodFromServer(string protocolName, HttpTransportType transportType, string path)
    {
        var protocol = HubProtocols[protocolName];
        await using (var server = await StartServer<Startup>())
        {
            var connection = CreateHubConnection(server.Url, path, transportType, protocol, LoggerFactory);
            var closeTcs = new TaskCompletionSource();
            connection.Closed += e =>
            {
                if (e != null)
                {
                    closeTcs.SetException(e);
                }
                else
                {
                    closeTcs.SetResult();
                }
                return Task.CompletedTask;
            };

            try
            {
                await connection.StartAsync().DefaultTimeout();
                await connection.InvokeAsync("CallHandlerThatDoesntExist").DefaultTimeout();
                await connection.DisposeAsync().DefaultTimeout();
                await closeTcs.Task.DefaultTimeout();
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} during test: {Message}", ex.GetType().Name, ex.Message);
                throw;
            }
            finally
            {
                await connection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Theory]
    [MemberData(nameof(HubProtocolsAndTransportsAndHubPaths))]
    [LogLevel(LogLevel.Trace)]
    public async Task CanStreamClientMethodFromServer(string protocolName, HttpTransportType transportType, string path)
    {
        var protocol = HubProtocols[protocolName];
        await using (var server = await StartServer<Startup>())
        {
            var connection = CreateHubConnection(server.Url, path, transportType, protocol, LoggerFactory);
            try
            {
                await connection.StartAsync().DefaultTimeout();

                var channel = await connection.StreamAsChannelAsync<int>("Stream", 5).DefaultTimeout();
                var results = await channel.ReadAndCollectAllAsync().DefaultTimeout();

                Assert.Equal(new[] { 0, 1, 2, 3, 4 }, results.ToArray());
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await connection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Theory]
    [MemberData(nameof(HubProtocolsAndTransportsAndHubPaths))]
    [LogLevel(LogLevel.Trace)]
    public async Task CanStreamToAndFromClientInSameInvocation(string protocolName, HttpTransportType transportType, string path)
    {
        var protocol = HubProtocols[protocolName];
        await using (var server = await StartServer<Startup>())
        {
            var connection = CreateHubConnection(server.Url, path, transportType, protocol, LoggerFactory);
            try
            {
                await connection.StartAsync().DefaultTimeout();

                var channelWriter = Channel.CreateBounded<string>(5);
                var channel = await connection.StreamAsChannelAsync<string>("StreamEcho", channelWriter.Reader).DefaultTimeout();

                await channelWriter.Writer.WriteAsync("1").AsTask().DefaultTimeout();
                Assert.Equal("1", await channel.ReadAsync().AsTask().DefaultTimeout());
                await channelWriter.Writer.WriteAsync("2").AsTask().DefaultTimeout();
                Assert.Equal("2", await channel.ReadAsync().AsTask().DefaultTimeout());
                channelWriter.Writer.Complete();

                var results = await channel.ReadAndCollectAllAsync().DefaultTimeout();
                Assert.Empty(results);
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await connection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Theory]
    [MemberData(nameof(HubProtocolsAndTransportsAndHubPaths))]
    [LogLevel(LogLevel.Trace)]
    public async Task CanStreamToServerWithIAsyncEnumerable(string protocolName, HttpTransportType transportType, string path)
    {
        var protocol = HubProtocols[protocolName];
        await using (var server = await StartServer<Startup>())
        {
            var connection = CreateHubConnection(server.Url, path, transportType, protocol, LoggerFactory);
            try
            {
                async IAsyncEnumerable<string> clientStreamData()
                {
                    var items = new string[] { "A", "B", "C", "D" };
                    foreach (var item in items)
                    {
                        await Task.Delay(10);
                        yield return item;
                    }
                }

                await connection.StartAsync().DefaultTimeout();

                var stream = clientStreamData();

                var channel = await connection.StreamAsChannelAsync<string>("StreamEcho", stream).DefaultTimeout();

                Assert.Equal("A", await channel.ReadAsync().AsTask().DefaultTimeout());
                Assert.Equal("B", await channel.ReadAsync().AsTask().DefaultTimeout());
                Assert.Equal("C", await channel.ReadAsync().AsTask().DefaultTimeout());
                Assert.Equal("D", await channel.ReadAsync().AsTask().DefaultTimeout());

                var results = await channel.ReadAndCollectAllAsync().DefaultTimeout();
                Assert.Empty(results);
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await connection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Theory]
    [MemberData(nameof(HubProtocolsAndTransportsAndHubPaths))]
    [LogLevel(LogLevel.Trace)]
    public async Task CanCancelIAsyncEnumerableClientToServerUpload(string protocolName, HttpTransportType transportType, string path)
    {
        var protocol = HubProtocols[protocolName];
        await using (var server = await StartServer<Startup>())
        {
            var connection = CreateHubConnection(server.Url, path, transportType, protocol, LoggerFactory);
            try
            {
                async IAsyncEnumerable<int> clientStreamData()
                {
                    for (var i = 0; i < 1000; i++)
                    {
                        yield return i;
                        await Task.Delay(10);
                    }
                }

                await connection.StartAsync().DefaultTimeout();
                var results = new List<int>();
                var stream = clientStreamData();
                var cts = new CancellationTokenSource();
                var ex = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                {
                    var channel = await connection.StreamAsChannelAsync<int>("StreamEchoInt", stream, cts.Token).DefaultTimeout();

                    while (await channel.WaitToReadAsync())
                    {
                        while (channel.TryRead(out var item))
                        {
                            results.Add(item);
                            cts.Cancel();
                        }
                    }
                });

                Assert.True(results.Count > 0 && results.Count < 1000);
                Assert.True(cts.IsCancellationRequested);
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await connection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Theory]
    [MemberData(nameof(HubProtocolsAndTransportsAndHubPaths))]
    [LogLevel(LogLevel.Trace)]
    public async Task StreamAsyncCanBeCanceledThroughGetAsyncEnumerator(string protocolName, HttpTransportType transportType, string path)
    {
        var protocol = HubProtocols[protocolName];
        await using (var server = await StartServer<Startup>())
        {
            var connection = CreateHubConnection(server.Url, path, transportType, protocol, LoggerFactory);
            try
            {
                await connection.StartAsync().DefaultTimeout();
                var stream = connection.StreamAsync<int>("Stream", 1000);
                var results = new List<int>();

                var cts = new CancellationTokenSource();

                var enumerator = stream.GetAsyncEnumerator(cts.Token);
                await Assert.ThrowsAsync<TaskCanceledException>(async () =>
                {
                    while (await enumerator.MoveNextAsync())
                    {
                        results.Add(enumerator.Current);
                        cts.Cancel();
                    }
                });

                Assert.True(results.Count > 0 && results.Count < 1000);
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await connection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Theory]
    [MemberData(nameof(HubProtocolsAndTransportsAndHubPaths))]
    [LogLevel(LogLevel.Trace)]
    public async Task CanCloseStreamMethodEarly(string protocolName, HttpTransportType transportType, string path)
    {
        var protocol = HubProtocols[protocolName];
        await using (var server = await StartServer<Startup>())
        {
            var connection = CreateHubConnection(server.Url, path, transportType, protocol, LoggerFactory);
            try
            {
                await connection.StartAsync().DefaultTimeout();

                var cts = new CancellationTokenSource();

                var channel = await connection.StreamAsChannelAsync<int>("Stream", 1000, cts.Token).DefaultTimeout();

                // Wait for the server to start streaming items
                await channel.WaitToReadAsync().AsTask().DefaultTimeout();

                cts.Cancel();

                var results = await channel.ReadAndCollectAllAsync(suppressExceptions: true).DefaultTimeout();

                Assert.True(results.Count > 0 && results.Count < 1000);

                // We should have been canceled.
                await Assert.ThrowsAsync<TaskCanceledException>(() => channel.Completion);
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await connection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Theory]
    [MemberData(nameof(HubProtocolsAndTransportsAndHubPaths))]
    [LogLevel(LogLevel.Trace)]
    public async Task StreamDoesNotStartIfTokenAlreadyCanceled(string protocolName, HttpTransportType transportType, string path)
    {
        var protocol = HubProtocols[protocolName];
        await using (var server = await StartServer<Startup>())
        {
            var connection = CreateHubConnection(server.Url, path, transportType, protocol, LoggerFactory);
            try
            {
                await connection.StartAsync().DefaultTimeout();

                var cts = new CancellationTokenSource();
                cts.Cancel();

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.StreamAsChannelAsync<int>("Stream", 5, cts.Token).DefaultTimeout());
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await connection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Theory]
    [MemberData(nameof(HubProtocolsAndTransportsAndHubPaths))]
    public async Task ExceptionFromStreamingSentToClient(string protocolName, HttpTransportType transportType, string path)
    {
        bool ExpectedErrors(WriteContext writeContext)
        {
            return writeContext.LoggerName == DefaultHubDispatcherLoggerName &&
                   writeContext.EventId.Name == "FailedInvokingHubMethod";
        }

        var protocol = HubProtocols[protocolName];
        await using (var server = await StartServer<Startup>(ExpectedErrors))
        {
            var connection = CreateHubConnection(server.Url, path, transportType, protocol, LoggerFactory);
            try
            {
                await connection.StartAsync().DefaultTimeout();
                var channel = await connection.StreamAsChannelAsync<int>("StreamException").DefaultTimeout();

                var ex = await Assert.ThrowsAsync<HubException>(() => channel.ReadAndCollectAllAsync().DefaultTimeout());
                Assert.Equal("An unexpected error occurred invoking 'StreamException' on the server. InvalidOperationException: Error occurred while streaming.", ex.Message);
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await connection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Theory]
    [MemberData(nameof(HubProtocolsAndTransportsAndHubPaths))]
    public async Task ServerThrowsHubExceptionIfHubMethodCannotBeResolved(string hubProtocolName, HttpTransportType transportType, string hubPath)
    {
        var hubProtocol = HubProtocols[hubProtocolName];
        await using (var server = await StartServer<Startup>())
        {
            var connection = CreateHubConnection(server.Url, hubPath, transportType, hubProtocol, LoggerFactory);
            try
            {
                await connection.StartAsync().DefaultTimeout();

                var ex = await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("!@#$%")).DefaultTimeout();
                Assert.Equal("Failed to invoke '!@#$%' due to an error on the server. HubException: Method does not exist.", ex.Message);
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await connection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Theory]
    [MemberData(nameof(HubProtocolsAndTransportsAndHubPaths))]
    public async Task ServerThrowsHubExceptionIfHubMethodCannotBeResolvedAndArgumentsPassedIn(string hubProtocolName, HttpTransportType transportType, string hubPath)
    {
        var hubProtocol = HubProtocols[hubProtocolName];
        await using (var server = await StartServer<Startup>())
        {
            var connection = CreateHubConnection(server.Url, hubPath, transportType, hubProtocol, LoggerFactory);
            try
            {
                await connection.StartAsync().DefaultTimeout();

                var ex = await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("!@#$%", 10, "test")).DefaultTimeout();
                Assert.Equal("Failed to invoke '!@#$%' due to an error on the server. HubException: Method does not exist.", ex.Message);
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await connection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Theory]
    [MemberData(nameof(HubProtocolsList))]
    public async Task ServerThrowsHubExceptionOnHubMethodArgumentCountMismatch(string hubProtocolName)
    {
        var hubProtocol = HubProtocols[hubProtocolName];
        await using (var server = await StartServer<Startup>())
        {
            var connection = CreateHubConnection(server.Url, "/default", HttpTransportType.LongPolling, hubProtocol, LoggerFactory);
            try
            {
                await connection.StartAsync().DefaultTimeout();

                var ex = await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("Echo", "p1", 42)).DefaultTimeout();
                Assert.Equal("Failed to invoke 'Echo' due to an error on the server. InvalidDataException: Invocation provides 2 argument(s) but target expects 1.", ex.Message);
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await connection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Theory]
    [MemberData(nameof(HubProtocolsAndTransportsAndHubPaths))]
    public async Task ServerThrowsHubExceptionOnHubMethodArgumentTypeMismatch(string hubProtocolName, HttpTransportType transportType, string hubPath)
    {
        var hubProtocol = HubProtocols[hubProtocolName];
        await using (var server = await StartServer<Startup>())
        {
            var connection = CreateHubConnection(server.Url, hubPath, transportType, hubProtocol, LoggerFactory);
            try
            {
                await connection.StartAsync().DefaultTimeout();

                var ex = await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("Echo", new[] { 42 })).DefaultTimeout();
                Assert.StartsWith("Failed to invoke 'Echo' due to an error on the server.", ex.Message);
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await connection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Theory]
    [MemberData(nameof(HubProtocolsAndTransportsAndHubPaths))]
    public async Task ServerThrowsHubExceptionIfStreamingHubMethodCannotBeResolved(string hubProtocolName, HttpTransportType transportType, string hubPath)
    {
        var hubProtocol = HubProtocols[hubProtocolName];
        await using (var server = await StartServer<Startup>())
        {
            var connection = CreateHubConnection(server.Url, hubPath, transportType, hubProtocol, LoggerFactory);
            try
            {
                await connection.StartAsync().DefaultTimeout();

                var channel = await connection.StreamAsChannelAsync<int>("!@#$%");
                var ex = await Assert.ThrowsAsync<HubException>(() => channel.ReadAndCollectAllAsync().DefaultTimeout());
                Assert.Equal("Failed to invoke '!@#$%' due to an error on the server. HubException: Method does not exist.", ex.Message);
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await connection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Theory]
    [MemberData(nameof(HubProtocolsAndTransportsAndHubPaths))]
    public async Task ServerThrowsHubExceptionOnStreamingHubMethodArgumentCountMismatch(string hubProtocolName, HttpTransportType transportType, string hubPath)
    {
        var hubProtocol = HubProtocols[hubProtocolName];
        await using (var server = await StartServer<Startup>())
        {
            var connection = CreateHubConnection(server.Url, hubPath, transportType, hubProtocol, LoggerFactory);
            try
            {
                await connection.StartAsync().DefaultTimeout();

                var channel = await connection.StreamAsChannelAsync<int>("Stream", 42, 42);
                var ex = await Assert.ThrowsAsync<HubException>(() => channel.ReadAndCollectAllAsync().DefaultTimeout());
                Assert.Equal("Failed to invoke 'Stream' due to an error on the server. InvalidDataException: Invocation provides 2 argument(s) but target expects 1.", ex.Message);
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await connection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Theory]
    [MemberData(nameof(HubProtocolsAndTransportsAndHubPaths))]
    public async Task ServerThrowsHubExceptionOnStreamingHubMethodArgumentTypeMismatch(string hubProtocolName, HttpTransportType transportType, string hubPath)
    {
        var hubProtocol = HubProtocols[hubProtocolName];
        await using (var server = await StartServer<Startup>())
        {
            var connection = CreateHubConnection(server.Url, hubPath, transportType, hubProtocol, LoggerFactory);
            try
            {
                await connection.StartAsync().DefaultTimeout();

                var channel = await connection.StreamAsChannelAsync<int>("Stream", "xyz");
                var ex = await Assert.ThrowsAsync<HubException>(() => channel.ReadAndCollectAllAsync().DefaultTimeout());
                Assert.Equal("Failed to invoke 'Stream' due to an error on the server. InvalidDataException: Error binding arguments. Make sure that the types of the provided values match the types of the hub method being invoked.", ex.Message);
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await connection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Theory]
    [MemberData(nameof(HubProtocolsAndTransportsAndHubPaths))]
    public async Task ServerThrowsHubExceptionIfNonStreamMethodInvokedWithStreamAsync(string hubProtocolName, HttpTransportType transportType, string hubPath)
    {
        var hubProtocol = HubProtocols[hubProtocolName];
        await using (var server = await StartServer<Startup>())
        {
            var connection = CreateHubConnection(server.Url, hubPath, transportType, hubProtocol, LoggerFactory);
            try
            {
                await connection.StartAsync().DefaultTimeout();
                var channel = await connection.StreamAsChannelAsync<int>("HelloWorld").DefaultTimeout();
                var ex = await Assert.ThrowsAsync<HubException>(() => channel.ReadAndCollectAllAsync()).DefaultTimeout();
                Assert.Equal("The client attempted to invoke the non-streaming 'HelloWorld' method with a streaming invocation.", ex.Message);
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await connection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Theory]
    [MemberData(nameof(HubProtocolsAndTransportsAndHubPaths))]
    public async Task ServerThrowsHubExceptionIfStreamMethodInvokedWithInvoke(string hubProtocolName, HttpTransportType transportType, string hubPath)
    {
        var hubProtocol = HubProtocols[hubProtocolName];
        await using (var server = await StartServer<Startup>())
        {
            var connection = CreateHubConnection(server.Url, hubPath, transportType, hubProtocol, LoggerFactory);
            try
            {
                await connection.StartAsync().DefaultTimeout();

                var ex = await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("Stream", 3)).DefaultTimeout();
                Assert.Equal("The client attempted to invoke the streaming 'Stream' method with a non-streaming invocation.", ex.Message);
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await connection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Theory]
    [MemberData(nameof(HubProtocolsAndTransportsAndHubPaths))]
    public async Task ServerThrowsHubExceptionIfBuildingAsyncEnumeratorIsNotPossible(string hubProtocolName, HttpTransportType transportType, string hubPath)
    {
        var hubProtocol = HubProtocols[hubProtocolName];
        await using (var server = await StartServer<Startup>())
        {
            var connection = CreateHubConnection(server.Url, hubPath, transportType, hubProtocol, LoggerFactory);
            try
            {
                await connection.StartAsync().DefaultTimeout();
                var channel = await connection.StreamAsChannelAsync<int>("StreamBroken").DefaultTimeout();
                var ex = await Assert.ThrowsAsync<HubException>(() => channel.ReadAndCollectAllAsync()).DefaultTimeout();
                Assert.Equal("The value returned by the streaming method 'StreamBroken' is not a ChannelReader<> or IAsyncEnumerable<>.", ex.Message);
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await connection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Theory]
    [MemberData(nameof(HubProtocolsList))]
    public async Task ServerLogsErrorIfClientInvokeCannotBeSerialized(string protocolName)
    {
        // Just to help sanity check that the right exception is thrown
        var exceptionSubstring = protocolName switch
        {
            "json" => "A possible object cycle was detected.",
            "newtonsoft-json" => "A possible object cycle was detected.",
            "messagepack" => "Failed to serialize Microsoft.AspNetCore.SignalR.Client.FunctionalTests.TestHub+Unserializable value.",
            var x => throw new Exception($"The test does not have an exception string for the protocol '{x}'!"),
        };

        var protocol = HubProtocols[protocolName];
        await using (var server = await StartServer<Startup>(write =>
        {
            return write.EventId.Name == "FailedWritingMessage" || write.EventId.Name == "ReceivedCloseWithError"
                || write.EventId.Name == "ShutdownWithError";
        }))
        {
            var connection = CreateHubConnection(server.Url, "/default", HttpTransportType.WebSockets, protocol, LoggerFactory);
            var closedTcs = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.Closed += (ex) => { closedTcs.TrySetResult(ex); return Task.CompletedTask; };
            try
            {
                await connection.StartAsync().DefaultTimeout();

                var result = connection.InvokeAsync<string>(nameof(TestHub.CallWithUnserializableObject));

                // The connection should close.
                var exception = await closedTcs.Task.DefaultTimeout();
                Assert.Contains("Connection closed with an error.", exception.Message);

                var hubException = await Assert.ThrowsAsync<HubException>(() => result).DefaultTimeout();
                Assert.Contains("Connection closed with an error.", hubException.Message);
                Assert.Contains(exceptionSubstring, hubException.Message);
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await connection.DisposeAsync().DefaultTimeout();
            }

            var errorLog = server.GetLogs().SingleOrDefault(r => r.Write.EventId.Name == "FailedWritingMessage");
            Assert.NotNull(errorLog);
            Assert.Contains(exceptionSubstring, errorLog.Write.Exception.Message);
            Assert.Equal(LogLevel.Error, errorLog.Write.LogLevel);
        }
    }

    [Theory]
    [MemberData(nameof(HubProtocolsList))]
    public async Task ServerLogsErrorIfReturnValueCannotBeSerialized(string protocolName)
    {
        // Just to help sanity check that the right exception is thrown
        var exceptionSubstring = protocolName switch
        {
            "json" => "A possible object cycle was detected.",
            "newtonsoft-json" => "A possible object cycle was detected.",
            "messagepack" => "Failed to serialize Microsoft.AspNetCore.SignalR.Client.FunctionalTests.TestHub+Unserializable value.",
            var x => throw new Exception($"The test does not have an exception string for the protocol '{x}'!"),
        };

        var protocol = HubProtocols[protocolName];
        await using (var server = await StartServer<Startup>(write =>
        {
            return write.EventId.Name == "FailedWritingMessage" || write.EventId.Name == "ReceivedCloseWithError"
                || write.EventId.Name == "ShutdownWithError";
        }))
        {
            var connection = CreateHubConnection(server.Url, "/default", HttpTransportType.LongPolling, protocol, LoggerFactory);
            var closedTcs = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            connection.Closed += (ex) => { closedTcs.TrySetResult(ex); return Task.CompletedTask; };
            try
            {
                await connection.StartAsync().DefaultTimeout();

                var result = connection.InvokeAsync<string>(nameof(TestHub.GetUnserializableObject)).DefaultTimeout();

                // The connection should close.
                var exception = await closedTcs.Task.DefaultTimeout();
                Assert.Contains("Connection closed with an error.", exception.Message);

                var hubException = await Assert.ThrowsAsync<HubException>(() => result).DefaultTimeout();
                Assert.Contains("Connection closed with an error.", hubException.Message);
                Assert.Contains(exceptionSubstring, hubException.Message);
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await connection.DisposeAsync().DefaultTimeout();
            }

            var errorLog = server.GetLogs().SingleOrDefault(r => r.Write.EventId.Name == "FailedWritingMessage");
            Assert.NotNull(errorLog);
            Assert.Contains(exceptionSubstring, errorLog.Write.Exception.Message);
            Assert.Equal(LogLevel.Error, errorLog.Write.LogLevel);
        }
    }

    [Fact]
    public async Task RandomGenericIsNotTreatedAsStream()
    {
        var hubPath = HubPaths[0];
        var hubProtocol = HubProtocols.First().Value;
        var transportType = TransportTypes().First().Cast<HttpTransportType>().First();

        await using (var server = await StartServer<Startup>())
        {
            var connection = CreateHubConnection(server.Url, hubPath, transportType, hubProtocol, LoggerFactory);
            await connection.StartAsync().DefaultTimeout();
            // List<T> will be looked at to replace with a StreamPlaceholder and should be skipped, so an error will be thrown from the
            // protocol on the server when it tries to match List<T> with a StreamPlaceholder
            var hubException = await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync<int>("StreamEcho", new List<string> { "1", "2" }).DefaultTimeout());
            Assert.Equal("Failed to invoke 'StreamEcho' due to an error on the server. InvalidDataException: Invocation provides 1 argument(s) but target expects 0.",
                hubException.Message);
            await connection.DisposeAsync().DefaultTimeout();
        }
    }

    [Theory]
    [MemberData(nameof(TransportTypes))]
    public async Task ClientCanUseJwtBearerTokenForAuthentication(HttpTransportType transportType)
    {
        await using (var server = await StartServer<Startup>())
        {
            async Task<string> AccessTokenProvider()
            {
                var httpResponse = await new HttpClient().GetAsync(server.Url + "/generateJwtToken");
                httpResponse.EnsureSuccessStatusCode();
                return await httpResponse.Content.ReadAsStringAsync();
            };

            var hubConnection = new HubConnectionBuilder()
                .WithLoggerFactory(LoggerFactory)
                .WithUrl(server.Url + "/authorizedhub", transportType, options =>
                {
                    options.AccessTokenProvider = AccessTokenProvider;
                })
                .Build();
            try
            {
                await hubConnection.StartAsync().DefaultTimeout();
                var message = await hubConnection.InvokeAsync<string>(nameof(TestHub.Echo), "Hello, World!").DefaultTimeout();
                Assert.Equal("Hello, World!", message);
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await hubConnection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Theory]
    [MemberData(nameof(TransportTypesWithAuth))]
    public async Task ClientWillFailAuthEndPointIfNotAuthorized(HttpTransportType transportType, string hubPath)
    {
        bool ExpectedErrors(WriteContext writeContext)
        {
            return writeContext.Exception is HttpRequestException;
        }

        await using (var server = await StartServer<Startup>(ExpectedErrors))
        {
            var hubConnection = new HubConnectionBuilder()
                .WithLoggerFactory(LoggerFactory)
                .WithUrl(server.Url + hubPath, transportType)
                .Build();
            try
            {
                var ex = await Assert.ThrowsAnyAsync<HttpRequestException>(() => hubConnection.StartAsync().DefaultTimeout());
                Assert.Equal("Response status code does not indicate success: 401 (Unauthorized).", ex.Message);
            }
            finally
            {
                await hubConnection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Theory]
    [MemberData(nameof(TransportTypes))]
    public async Task ClientCanUseJwtBearerTokenForAuthenticationWhenRedirected(HttpTransportType transportType)
    {
        await using (var server = await StartServer<Startup>())
        {
            var hubConnection = new HubConnectionBuilder()
                .WithLoggerFactory(LoggerFactory)
                .WithUrl(server.Url + "/redirect", transportType)
                .Build();
            try
            {
                await hubConnection.StartAsync().DefaultTimeout();
                var message = await hubConnection.InvokeAsync<string>(nameof(TestHub.Echo), "Hello, World!").DefaultTimeout();
                Assert.Equal("Hello, World!", message);
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await hubConnection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Theory]
    [MemberData(nameof(TransportTypes))]
    public async Task ClientCanSendHeaders(HttpTransportType transportType)
    {
        await using (var server = await StartServer<Startup>())
        {
            var hubConnection = new HubConnectionBuilder()
                .WithLoggerFactory(LoggerFactory)
                .WithUrl(server.Url + "/default", transportType, options =>
                {
                    options.Headers["X-test"] = "42";
                    options.Headers["X-42"] = "test";
                })
                .Build();
            try
            {
                await hubConnection.StartAsync().DefaultTimeout();
                var headerValues = await hubConnection.InvokeAsync<string[]>(nameof(TestHub.GetHeaderValues), new[] { "X-test", "X-42" }).DefaultTimeout();
                Assert.Equal(new[] { "42", "test" }, headerValues);
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await hubConnection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Fact]
    public async Task UserAgentIsSet()
    {
        await using (var server = await StartServer<Startup>())
        {
            var hubConnection = new HubConnectionBuilder()
                .WithLoggerFactory(LoggerFactory)
                .WithUrl(server.Url + "/default", HttpTransportType.LongPolling, options =>
                {
                    options.Headers["X-test"] = "42";
                    options.Headers["X-42"] = "test";
                })
                .Build();
            try
            {
                await hubConnection.StartAsync().DefaultTimeout();
                var headerValues = await hubConnection.InvokeAsync<string[]>(nameof(TestHub.GetHeaderValues), new[] { "User-Agent" }).DefaultTimeout();
                Assert.NotNull(headerValues);
                Assert.Single(headerValues);

                var userAgent = headerValues[0];

                Assert.StartsWith("Microsoft SignalR/", userAgent);

                var majorVersion = typeof(HttpConnection).Assembly.GetName().Version.Major;
                var minorVersion = typeof(HttpConnection).Assembly.GetName().Version.Minor;

                Assert.Contains($"{majorVersion}.{minorVersion}", userAgent);

            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await hubConnection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Fact]
    public async Task UserAgentCanBeCleared()
    {
        await using (var server = await StartServer<Startup>())
        {
            var hubConnection = new HubConnectionBuilder()
                .WithLoggerFactory(LoggerFactory)
                .WithUrl(server.Url + "/default", HttpTransportType.LongPolling, options =>
                {
                    options.Headers["User-Agent"] = "";
                })
                .Build();
            try
            {
                await hubConnection.StartAsync().DefaultTimeout();
                var headerValues = await hubConnection.InvokeAsync<string[]>(nameof(TestHub.GetHeaderValues), new[] { "User-Agent" }).DefaultTimeout();
                Assert.NotNull(headerValues);
                Assert.Single(headerValues);

                var userAgent = headerValues[0];

                Assert.Null(userAgent);
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await hubConnection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Fact]
    public async Task UserAgentCanBeSet()
    {
        await using (var server = await StartServer<Startup>())
        {
            var hubConnection = new HubConnectionBuilder()
                .WithLoggerFactory(LoggerFactory)
                .WithUrl(server.Url + "/default", HttpTransportType.LongPolling, options =>
                {
                    options.Headers["User-Agent"] = "User Value";
                })
                .Build();
            try
            {
                await hubConnection.StartAsync().DefaultTimeout();
                var headerValues = await hubConnection.InvokeAsync<string[]>(nameof(TestHub.GetHeaderValues), new[] { "User-Agent" }).DefaultTimeout();
                Assert.NotNull(headerValues);
                Assert.Single(headerValues);

                var userAgent = headerValues[0];

                Assert.Equal("User Value", userAgent);
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await hubConnection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [ConditionalFact]
    [WebSocketsSupportedCondition]
    public async Task WebSocketOptionsAreApplied()
    {
        await using (var server = await StartServer<Startup>())
        {
            // System.Net has a HttpTransportType type which means we need to fully-qualify this rather than 'use' the namespace
            var cookieJar = new System.Net.CookieContainer();
            cookieJar.Add(new System.Net.Cookie("Foo", "Bar", "/", new Uri(server.Url).Host));

            var hubConnection = new HubConnectionBuilder()
                .WithLoggerFactory(LoggerFactory)
                .WithUrl(server.Url + "/default", HttpTransportType.WebSockets, options =>
                {
                    options.WebSocketConfiguration = o => o.Cookies = cookieJar;
                })
                .Build();
            try
            {
                await hubConnection.StartAsync().DefaultTimeout();
                var cookieValue = await hubConnection.InvokeAsync<string>(nameof(TestHub.GetCookieValue), "Foo").DefaultTimeout();
                Assert.Equal("Bar", cookieValue);
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await hubConnection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [ConditionalFact]
    [WebSocketsSupportedCondition]
    public async Task CookiesFromNegotiateAreAppliedToWebSockets()
    {
        await using (var server = await StartServer<Startup>())
        {
            var hubConnection = new HubConnectionBuilder()
                .WithLoggerFactory(LoggerFactory)
                .WithUrl(server.Url + "/default", HttpTransportType.WebSockets)
                .Build();
            try
            {
                await hubConnection.StartAsync().DefaultTimeout();
                var cookieValue = await hubConnection.InvokeAsync<string>(nameof(TestHub.GetCookieValue), "fromNegotiate").DefaultTimeout();
                Assert.Equal("a value", cookieValue);
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await hubConnection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Fact]
    public async Task CheckHttpConnectionFeatures()
    {
        await using (var server = await StartServer<Startup>())
        {
            var hubConnection = new HubConnectionBuilder()
                .WithLoggerFactory(LoggerFactory)
                .WithUrl(server.Url + "/default")
                .Build();
            try
            {
                await hubConnection.StartAsync().DefaultTimeout();

                var features = await hubConnection.InvokeAsync<JsonElement[]>(nameof(TestHub.GetIHttpConnectionFeatureProperties)).DefaultTimeout();
                var localPort = features[0].GetInt64();
                var remotePort = features[1].GetInt64();
                var localIP = features[2].GetString();
                var remoteIP = features[3].GetString();

                Assert.True(localPort > 0L);
                Assert.True(remotePort > 0L);
                Assert.Equal("127.0.0.1", localIP);
                Assert.Equal("127.0.0.1", remoteIP);
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await hubConnection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Fact]
    public async Task UserIdProviderCanAccessHttpContext()
    {
        await using (var server = await StartServer<Startup>())
        {
            var hubConnection = new HubConnectionBuilder()
                .WithLoggerFactory(LoggerFactory)
                .WithUrl(server.Url + "/default", options =>
                {
                    options.Headers.Add(HeaderUserIdProvider.HeaderName, "SuperAdmin");
                })
                .Build();
            try
            {
                await hubConnection.StartAsync().DefaultTimeout();

                var userIdentifier = await hubConnection.InvokeAsync<string>(nameof(TestHub.GetUserIdentifier)).DefaultTimeout();
                Assert.Equal("SuperAdmin", userIdentifier);
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await hubConnection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Fact]
    public async Task NegotiationSkipsServerSentEventsWhenUsingBinaryProtocol()
    {
        await using (var server = await StartServer<Startup>())
        {
            var hubConnectionBuilder = new HubConnectionBuilder()
                .WithLoggerFactory(LoggerFactory)
                .AddMessagePackProtocol()
                .WithUrl(server.Url + "/default-nowebsockets");

            var hubConnection = hubConnectionBuilder.Build();
            try
            {
                await hubConnection.StartAsync().DefaultTimeout();

                var transport = await hubConnection.InvokeAsync<HttpTransportType>(nameof(TestHub.GetActiveTransportName)).DefaultTimeout();
                Assert.Equal(HttpTransportType.LongPolling, transport);
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await hubConnection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Fact]
    public async Task StopCausesPollToReturnImmediately()
    {
        await using (var server = await StartServer<Startup>())
        {
            PollTrackingMessageHandler pollTracker = null;
            var hubConnection = new HubConnectionBuilder()
                .WithLoggerFactory(LoggerFactory)
                .WithUrl(server.Url + "/default", options =>
                {
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = handler =>
                    {
                        pollTracker = new PollTrackingMessageHandler(handler);
                        return pollTracker;
                    };
                })
                .Build();

            await hubConnection.StartAsync();

            Assert.NotNull(pollTracker);
            Assert.NotNull(pollTracker.ActivePoll);

            var stopTask = hubConnection.StopAsync();

            try
            {
                // if we completed running before the poll or after the poll started then the task
                // might complete successfully
                await pollTracker.ActivePoll.DefaultTimeout();
            }
            catch (OperationCanceledException)
            {
                // If this happens it's fine because we were in the middle of a poll
            }

            await stopTask;
        }
    }

    [Theory]
    [MemberData(nameof(TransportTypes))]
    public async Task CanAutomaticallyReconnect(HttpTransportType transportType)
    {
        bool ExpectedErrors(WriteContext writeContext)
        {
            return writeContext.LoggerName == typeof(HubConnection).FullName &&
                   writeContext.EventId.Name == "ReconnectingWithError";
        }

        await using (var server = await StartServer<Startup>(ExpectedErrors))
        {
            var connection = CreateHubConnection(
                server.Url,
                path: HubPaths.First(),
                transportType: transportType,
                loggerFactory: LoggerFactory,
                withAutomaticReconnect: true);

            try
            {
                var echoMessage = "test";
                var reconnectingTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var reconnectedTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

                connection.Reconnecting += _ =>
                {
                    reconnectingTcs.SetResult();
                    return Task.CompletedTask;
                };

                connection.Reconnected += connectionId =>
                {
                    reconnectedTcs.SetResult(connectionId);
                    return Task.CompletedTask;
                };


                await connection.StartAsync().DefaultTimeout();
                var initialConnectionId = connection.ConnectionId;

                connection.OnServerTimeout();

                await reconnectingTcs.Task.DefaultTimeout();
                var newConnectionId = await reconnectedTcs.Task.DefaultTimeout();
                Assert.NotEqual(initialConnectionId, newConnectionId);
                Assert.Equal(connection.ConnectionId, newConnectionId);

                var result = await connection.InvokeAsync<string>(nameof(TestHub.Echo), echoMessage).DefaultTimeout();
                Assert.Equal(echoMessage, result);
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await connection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Fact]
    public async Task CanAutomaticallyReconnectAfterRedirect()
    {
        bool ExpectedErrors(WriteContext writeContext)
        {
            return writeContext.LoggerName == typeof(HubConnection).FullName &&
                   writeContext.EventId.Name == "ReconnectingWithError";
        }

        await using (var server = await StartServer<Startup>(ExpectedErrors))
        {
            var connection = new HubConnectionBuilder()
                .WithLoggerFactory(LoggerFactory)
                .WithUrl(server.Url + "/redirect")
                .WithAutomaticReconnect()
                .Build();

            try
            {
                var echoMessage = "test";
                var reconnectingTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var reconnectedTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

                connection.Reconnecting += _ =>
                {
                    reconnectingTcs.SetResult();
                    return Task.CompletedTask;
                };

                connection.Reconnected += connectionId =>
                {
                    reconnectedTcs.SetResult(connectionId);
                    return Task.CompletedTask;
                };

                await connection.StartAsync().DefaultTimeout();
                var initialConnectionId = connection.ConnectionId;

                connection.OnServerTimeout();

                await reconnectingTcs.Task.DefaultTimeout();
                var newConnectionId = await reconnectedTcs.Task.DefaultTimeout();
                Assert.NotEqual(initialConnectionId, newConnectionId);
                Assert.Equal(connection.ConnectionId, newConnectionId);

                var result = await connection.InvokeAsync<string>(nameof(TestHub.Echo), echoMessage).DefaultTimeout();
                Assert.Equal(echoMessage, result);
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await connection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    [Fact]
    public async Task CanAutomaticallyReconnectAfterSkippingNegotiation()
    {
        bool ExpectedErrors(WriteContext writeContext)
        {
            return writeContext.LoggerName == typeof(HubConnection).FullName &&
                   writeContext.EventId.Name == "ReconnectingWithError";
        }

        await using (var server = await StartServer<Startup>(ExpectedErrors))
        {
            var connectionBuilder = new HubConnectionBuilder()
                .WithLoggerFactory(LoggerFactory)
                .WithUrl(server.Url + HubPaths.First(), HttpTransportType.WebSockets)
                .WithAutomaticReconnect();

            connectionBuilder.Services.Configure<HttpConnectionOptions>(o =>
            {
                o.SkipNegotiation = true;
            });

            var connection = connectionBuilder.Build();

            try
            {
                var echoMessage = "test";
                var reconnectingTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var reconnectedTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

                connection.Reconnecting += _ =>
                {
                    reconnectingTcs.SetResult();
                    return Task.CompletedTask;
                };

                connection.Reconnected += connectionId =>
                {
                    reconnectedTcs.SetResult(connectionId);
                    return Task.CompletedTask;
                };

                await connection.StartAsync().DefaultTimeout();
                Assert.Null(connection.ConnectionId);

                connection.OnServerTimeout();

                await reconnectingTcs.Task.DefaultTimeout();
                var newConnectionId = await reconnectedTcs.Task.DefaultTimeout();
                Assert.Null(newConnectionId);
                Assert.Null(connection.ConnectionId);

                var result = await connection.InvokeAsync<string>(nameof(TestHub.Echo), echoMessage).DefaultTimeout();
                Assert.Equal(echoMessage, result);
            }
            catch (Exception ex)
            {
                LoggerFactory.CreateLogger<HubConnectionTests>().LogError(ex, "{ExceptionType} from test", ex.GetType().FullName);
                throw;
            }
            finally
            {
                await connection.DisposeAsync().DefaultTimeout();
            }
        }
    }

    private class PollTrackingMessageHandler : DelegatingHandler
    {
        public Task<HttpResponseMessage> ActivePoll { get; private set; }

        public PollTrackingMessageHandler(HttpMessageHandler innerHandler) : base(innerHandler)
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                ActivePoll = base.SendAsync(request, cancellationToken);
                return ActivePoll;
            }

            return base.SendAsync(request, cancellationToken);
        }
    }

    public static IEnumerable<object[]> HubProtocolsAndTransportsAndHubPaths
    {
        get
        {
            foreach (var protocol in HubProtocols)
            {
                foreach (var transport in TransportTypes().SelectMany(t => t).Cast<HttpTransportType>())
                {
                    foreach (var hubPath in HubPaths)
                    {
                        if (!(protocol.Value is MessagePackHubProtocol) || transport != HttpTransportType.ServerSentEvents)
                        {
                            yield return new object[] { protocol.Key, transport, hubPath };
                        }
                    }
                }
            }
        }
    }

    public static IEnumerable<object[]> TransportTypesWithAuth()
    {
        foreach (var transport in TransportTypes().SelectMany(t => t).Cast<HttpTransportType>())
        {
            foreach (var path in new[] { "/authorizedhub", "/authorizedhub2" })
            {
                yield return new object[] { transport, path };
            }
        }
    }

    public static IEnumerable<object[]> HubProtocolsList
    {
        get
        {
            foreach (var protocol in HubProtocols)
            {
                yield return new object[] { protocol.Key };
            }
        }
    }

    // This list excludes "special" hub paths like "default-nowebsockets" which exist for specific tests.
    public static string[] HubPaths = new[] { "/default", "/dynamic", "/hubT" };

    public static Dictionary<string, IHubProtocol> HubProtocols =>
        new Dictionary<string, IHubProtocol>
        {
                { "json", new JsonHubProtocol() },
                { "newtonsoft-json", new NewtonsoftJsonHubProtocol() },
                { "messagepack", new MessagePackHubProtocol() },
        };

    public static IEnumerable<object[]> TransportTypes()
    {
        if (TestHelpers.IsWebSocketsSupported())
        {
            yield return new object[] { HttpTransportType.WebSockets };
        }
        yield return new object[] { HttpTransportType.ServerSentEvents };
        yield return new object[] { HttpTransportType.LongPolling };
    }
}
