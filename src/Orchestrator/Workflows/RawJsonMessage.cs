using System.Text;
using Rebus.Messages;
using Rebus.Serialization;

namespace RebusDemo.Orchestrator.Workflows;

/// <summary>A message the orchestrator sends as raw JSON under a registered message type name.</summary>
internal sealed record RawJsonMessage(string MessageType, string Json);

/// <summary>Writes a <see cref="RawJsonMessage"/> as the message type it names, and leaves other messages alone.</summary>
internal sealed class RawJsonSerializer(ISerializer inner) : ISerializer
{
    public Task<TransportMessage> Serialize(Message message)
    {
        if (message.Body is not RawJsonMessage raw)
            return inner.Serialize(message);

        // The receiving service deserializes by this header, so it sees an ordinary Rebus message.
        var headers = new Dictionary<string, string>(message.Headers)
        {
            [Headers.Type] = raw.MessageType,
            [Headers.ContentType] = "application/json;charset=utf-8"
        };
        return Task.FromResult(new TransportMessage(headers, Encoding.UTF8.GetBytes(raw.Json)));
    }

    public Task<Message> Deserialize(TransportMessage transportMessage) => inner.Deserialize(transportMessage);
}
