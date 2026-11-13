using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace PrintBridge.IntegrationTests;

public sealed class BridgeTests
{
	private static BridgeTarget Destination(byte address = 2)
	{
		WifiAdapter wifi = new WifiAdapter(Guid.NewGuid(), "Wi-Fi", "Test", 1, IPAddress.Parse("10.1.2.1"), 24, null, null, 1u);
		NetworkPrinter device = new NetworkPrinter(new IPAddress(new byte[4] { 10, 1, 2, address }), null, null, "010203040506", null, null, Raw: true, Ipp: false, Lpr: false, "fixture");
		return new BridgeTarget(wifi, device, PrinterIdentity.Confirm(device));
	}

	[Fact]
	public async Task BinaryPayloadSurvivesHalfCloseInBothDirections()
	{
		CancellationTokenSource deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10L));
		TcpListener printer;
		byte[] job;
		byte[] response;
		try
		{
			printer = Listener();
			try
			{
				int port = FreePort();
				await using TcpBridgeService bridge = new TcpBridgeService();
				bridge.SetTarget(Destination(2));
				bridge.Start(port, 4, TimeSpan.FromSeconds(5L), (BridgeTarget _, CancellationToken token) => ConnectAsync(Port(printer), token));
				job = RandomNumberGenerator.GetBytes(250123);
				response = RandomNumberGenerator.GetBytes(57001);
				Task run = EmulatePrinter();
				using TcpClient client = await ConnectAsync(port, deadline.Token);
				NetworkStream local = client.GetStream();
				await local.WriteAsync(job, deadline.Token);
				client.Client.Shutdown(SocketShutdown.Send);
				using MemoryStream returned = new MemoryStream();
				await local.CopyToAsync(returned, deadline.Token);
				Assert.Equal(response, returned.ToArray());
				await run;
			}
			finally
			{
				if (printer != null)
				{
					((IDisposable)printer).Dispose();
				}
			}
		}
		finally
		{
			if (deadline != null)
			{
				((IDisposable)deadline).Dispose();
			}
		}
		async Task EmulatePrinter()
		{
			using TcpClient incoming = await printer.AcceptTcpClientAsync(deadline.Token);
			NetworkStream stream = incoming.GetStream();
			using MemoryStream received = new MemoryStream();
			await stream.CopyToAsync(received, deadline.Token);
			Assert.Equal(job, received.ToArray());
			await stream.WriteAsync(response, deadline.Token);
			incoming.Client.Shutdown(SocketShutdown.Send);
		}
	}

	[Fact]
	public async Task DestinationChangesWithoutRebindingListener()
	{
		using CancellationTokenSource deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10L));
		TcpListener first = Listener();
		try
		{
			TcpListener second = Listener();
			try
			{
				int port = FreePort();
				await using TcpBridgeService bridge = new TcpBridgeService();
				BridgeTarget original = Destination(2);
				bridge.SetTarget(original);
				bridge.Start(port, 4, TimeSpan.FromSeconds(5L), (BridgeTarget target, CancellationToken token) => ConnectAsync(Port((target == original) ? first : second), token));
				using TcpClient client1 = await ConnectAsync(port, deadline.Token);
				using TcpClient peer1 = await first.AcceptTcpClientAsync(deadline.Token);
				bridge.SetTarget(Destination(3));
				using TcpClient client2 = await ConnectAsync(port, deadline.Token);
				using TcpClient peer2 = await second.AcceptTcpClientAsync(deadline.Token);
				await client1.GetStream().WriteAsync(new byte[1] { 101 }, deadline.Token);
				await client2.GetStream().WriteAsync(new byte[1] { 202 }, deadline.Token);
				byte[] buffer = new byte[1];
				await peer1.GetStream().ReadExactlyAsync(buffer, deadline.Token);
				Assert.Equal(101, buffer[0]);
				await peer2.GetStream().ReadExactlyAsync(buffer, deadline.Token);
				Assert.Equal(202, buffer[0]);
				Assert.Equal(2, bridge.Active);
			}
			finally
			{
				if (second != null)
				{
					((IDisposable)second).Dispose();
				}
			}
		}
		finally
		{
			if (first != null)
			{
				((IDisposable)first).Dispose();
			}
		}
	}

	[Fact]
	public async Task OccupiedPortAndUnreachableDestinationAreReported()
	{
		TcpListener occupied = Listener();
		try
		{
			TcpBridgeService bridge = new TcpBridgeService();
			try
			{
				Assert.Throws<SocketException>(() =>
				{
					bridge.Start(Port(occupied), 4, TimeSpan.FromSeconds(5L), (BridgeTarget _, CancellationToken token) => ConnectAsync(Port(occupied), token));
				});
				Assert.False(bridge.Running);
				using CancellationTokenSource deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10L));
				int port = FreePort();
				TaskCompletionSource<Exception> error = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
				bridge.SetTarget(Destination(2));
				bridge.Start(port, 4, TimeSpan.FromSeconds(5L), (BridgeTarget _, CancellationToken _) => Task.FromException<TcpClient>(new SocketException(10065)), (Exception ex) =>
				{
					error.SetResult(ex);
				});
				using (await ConnectAsync(port, deadline.Token))
				{
					IOException failure = Assert.IsType<IOException>(await error.Task.WaitAsync(deadline.Token));
					Assert.IsType<SocketException>(failure.InnerException);
					Assert.Contains("Connecting to printer", failure.Message, StringComparison.Ordinal);
					Assert.NotNull(bridge.LastError);
				}
			}
			finally
			{
				if (bridge != null)
				{
					await bridge.DisposeAsync();
				}
			}
		}
		finally
		{
			if (occupied != null)
			{
				((IDisposable)occupied).Dispose();
			}
		}
	}

	private static int Port(TcpListener listener)
	{
		return ((IPEndPoint)listener.LocalEndpoint).Port;
	}

	private static TcpListener Listener()
	{
		TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		return listener;
	}

	private static int FreePort()
	{
		using TcpListener listener = Listener();
		return Port(listener);
	}

	private static async Task<TcpClient> ConnectAsync(int port, CancellationToken token)
	{
		TcpClient client = new TcpClient();
		await client.ConnectAsync(IPAddress.Loopback, port, token);
		return client;
	}
}
