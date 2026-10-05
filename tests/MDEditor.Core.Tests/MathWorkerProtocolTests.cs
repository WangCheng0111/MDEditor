using System.Text.Json;
using MDEditor.Typesetting.Mathematics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MDEditor.Core.Tests;

[TestClass]
public sealed class MathWorkerProtocolTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public void Default_handshake_negotiates_the_current_layout_protocol()
    {
        var request = new MathWorkerHandshakeRequest { ClientId = "test-client" };
        request.Validate();
        Assert.AreEqual(MathLayoutRequest.CurrentProtocolVersion, request.MinimumProtocolVersion);
        Assert.AreEqual(MathLayoutRequest.CurrentProtocolVersion, request.MaximumProtocolVersion);
        Assert.AreEqual(MathWorkerHandshakeRequest.CurrentTransportVersion, request.TransportVersion);
    }

    [TestMethod]
    public void Handshake_requires_the_hello_message_type() => Assert.Throws<ArgumentException>(() =>
        new MathWorkerHandshakeRequest { ClientId = "test", MessageType = "request" }.Validate());

    [TestMethod]
    public void Handshake_rejects_an_unknown_transport() => Assert.Throws<ArgumentException>(() =>
        new MathWorkerHandshakeRequest { ClientId = "test", TransportVersion = 2 }.Validate());

    [TestMethod]
    [DataRow(0, 1)]
    [DataRow(2, 1)]
    [DataRow(-1, -1)]
    public void Handshake_rejects_invalid_protocol_ranges(int minimum, int maximum) =>
        Assert.Throws<ArgumentException>(() => new MathWorkerHandshakeRequest
        {
            ClientId = "test", MinimumProtocolVersion = minimum, MaximumProtocolVersion = maximum
        }.Validate());

    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    public void Handshake_requires_a_client_identity(string clientId) => Assert.Throws<ArgumentException>(() =>
        new MathWorkerHandshakeRequest { ClientId = clientId }.Validate());

    [TestMethod]
    public void Handshake_metadata_round_trips()
    {
        var response = new MathWorkerHandshakeResponse
        {
            Success = true, SelectedProtocolVersion = 1, ProcessId = 42,
            SessionId = "session", CacheCapacity = 128
        };
        var copy = JsonSerializer.Deserialize<MathWorkerHandshakeResponse>(JsonSerializer.Serialize(response, Json), Json)!;
        Assert.IsTrue(copy.Success);
        Assert.AreEqual("ready", copy.MessageType);
        Assert.AreEqual(42, copy.ProcessId);
        Assert.AreEqual("session", copy.SessionId);
        Assert.AreEqual(128, copy.CacheCapacity);
    }

    [TestMethod]
    public void Persistent_response_metadata_round_trips()
    {
        var response = new MathWorkerResponse
        {
            RequestId = "request", Success = true, SessionId = "session", Sequence = 7, CacheHit = true
        };
        var copy = JsonSerializer.Deserialize<MathWorkerResponse>(JsonSerializer.Serialize(response, Json), Json)!;
        Assert.AreEqual("request", copy.RequestId);
        Assert.AreEqual("session", copy.SessionId);
        Assert.AreEqual(7, copy.Sequence);
        Assert.IsTrue(copy.CacheHit);
    }

    [TestMethod]
    public void Legacy_one_shot_response_remains_deserializable()
    {
        var response = JsonSerializer.Deserialize<MathWorkerResponse>(
            "{\"protocolVersion\":1,\"requestId\":\"old\",\"success\":false}", Json)!;
        Assert.AreEqual("old", response.RequestId);
        Assert.AreEqual("", response.SessionId);
        Assert.AreEqual(0, response.Sequence);
        Assert.IsFalse(response.CacheHit);
    }

    [TestMethod]
    public void Formula_parse_error_carries_a_source_relative_position()
    {
        var response = new MathWorkerResponse
        {
            RequestId = "broken", SessionId = "session", Sequence = 8,
            Success = false, ErrorCode = "invalid-formula",
            ErrorMessage = "Unsupported command", ErrorPosition = 3
        };
        var copy = JsonSerializer.Deserialize<MathWorkerResponse>(
            JsonSerializer.Serialize(response, Json), Json)!;
        Assert.AreEqual("invalid-formula", copy.ErrorCode);
        Assert.AreEqual(3, copy.ErrorPosition);
    }
}
