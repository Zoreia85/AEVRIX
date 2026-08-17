using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Aevrix.Core.Tests;

[TestClass]
public sealed class SpecialistLabExpandedRoutingTests
{
    [TestMethod]
    public void HttpAndWebSocketExposeCleartextRiskWhileSecureVariantsDoNot()
    {
        var http = TargetIntakeRouter.ClassifyOnline(new Uri("http://example.test/app"));
        var https = TargetIntakeRouter.ClassifyOnline(new Uri("https://example.test/app"));
        var ws = TargetIntakeRouter.ClassifyOnline(new Uri("ws://example.test/socket"));
        var wss = TargetIntakeRouter.ClassifyOnline(new Uri("wss://example.test/socket"));

        Assert.AreEqual(TargetKind.HttpWebApplication, http.Kind);
        Assert.IsTrue(http.RequiresTransportRiskAcknowledgement);
        Assert.AreEqual(TargetKind.HttpsWebApplication, https.Kind);
        Assert.IsFalse(https.RequiresTransportRiskAcknowledgement);
        Assert.AreEqual(TargetKind.WebSocketEndpoint, ws.Kind);
        Assert.IsTrue(ws.RequiresTransportRiskAcknowledgement);
        Assert.AreEqual(TargetKind.SecureWebSocketEndpoint, wss.Kind);
        Assert.IsFalse(wss.RequiresTransportRiskAcknowledgement);
    }

    [TestMethod]
    public void CommonOnlineProtocolsRouteToWebOnlineLab()
    {
        var uris = new[]
        {
            "grpc://example.test/service",
            "grpcs://example.test/service",
            "ftp://example.test/files",
            "ftps://example.test/files",
            "sftp://example.test/files",
            "mqtt://example.test/topic",
            "mqtts://example.test/topic",
            "amqp://example.test/queue",
            "amqps://example.test/queue",
            "ipfs://bafyexample",
            "ipns://example.test"
        };

        foreach (var uri in uris)
        {
            var route = TargetIntakeRouter.ClassifyOnline(new Uri(uri));
            Assert.AreEqual(SpecialistLab.WebOnline, route.Lab, uri);
            Assert.IsTrue(route.RequiresContentVerification, uri);
        }
    }

    [TestMethod]
    public void BlockchainRpcRequiresDeclaredNetworkAndSupportedTransport()
    {
        var route = TargetIntakeRouter.ClassifyBlockchainEndpoint(
            new Uri("https://rpc.example.test"),
            "synthetic-chain");

        Assert.AreEqual(SpecialistLab.WebOnline, route.Lab);
        Assert.AreEqual(TargetKind.BlockchainRpcEndpoint, route.Kind);
        Assert.AreEqual(RoutingEvidenceStrength.ExplicitProtocolDeclaration, route.EvidenceStrength);
        Assert.AreEqual(TransportSecurity.EncryptedTransport, route.TransportSecurity);

        Assert.ThrowsExactly<ArgumentException>(() =>
            TargetIntakeRouter.ClassifyBlockchainEndpoint(new Uri("ftp://rpc.example.test"), "synthetic-chain"));
    }

    [TestMethod]
    public void ExpandedArtifactFamiliesRouteToDesktopOffline()
    {
        var cases = new[]
        {
            ("sample.zip", TargetKind.ArchiveContainer),
            ("sample.qcow2", TargetKind.VirtualMachineImage),
            ("sample.wasm", TargetKind.WebAssemblyModule),
            ("sample.sqlite", TargetKind.DatabaseArtifact),
            ("sample.pdf", TargetKind.DocumentArtifact),
            ("sample.json", TargetKind.StructuredDataArtifact),
            ("sample.pcapng", TargetKind.NetworkCapture),
            ("sample.sol", TargetKind.SmartContractSource),
            ("sample.abi", TargetKind.BlockchainArtifact),
            ("sample.uf2", TargetKind.FirmwareArtifact)
        };

        foreach (var (fileName, expectedKind) in cases)
        {
            var route = TargetIntakeRouter.ClassifyArtifact(Path.Combine(Path.GetTempPath(), fileName));
            Assert.AreEqual(SpecialistLab.DesktopOffline, route.Lab, fileName);
            Assert.AreEqual(expectedKind, route.Kind, fileName);
            Assert.AreEqual(TransportSecurity.LocalArtifact, route.TransportSecurity, fileName);
        }
    }
}
