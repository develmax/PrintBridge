using System;
using System.IO;
using System.Net;
using Microsoft.Extensions.Logging;
using Xunit;

namespace PrintBridge.IntegrationTests;

public sealed class DiagnosticsTests
{
	[Fact]
	public void LoggingIpv4DoesNotInterruptApplication()
	{
		string directory = Path.Combine(Path.GetTempPath(), $"PrintBridge-tests-{Guid.NewGuid():N}");
		try
		{
			using DiagnosticsService diagnostics = new DiagnosticsService(directory);
			diagnostics.CreateLogger("DiagnosticsTests").LogInformation("Verified address {Address}", IPAddress.Parse("192.0.2.3"));
			string output = File.ReadAllText(Path.Combine(directory, "printbridge.jsonl"));
			Assert.Contains("\"Address\":\"192.0.2.3\"", output, StringComparison.Ordinal);
		}
		finally
		{
			File.Delete(Path.Combine(directory, "printbridge.jsonl"));
			if (Directory.Exists(directory))
			{
				Directory.Delete(directory);
			}
		}
	}
}
