using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO.Pipes;
using System.Text;

namespace HotkeyListener.Services;

internal sealed class WindowerClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.General)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _pipeName;

    public WindowerClient(string pipeName)
    {
        _pipeName = pipeName;
    }

    public void ShowVariant(string sessionId, string variantName, string text) =>
        SendStructuredMessage("SHOW_VARIANT", new VariantPayload(sessionId, variantName, text));

    public void ShowRhombus() => SendMessage("SHOW_RHOMBUS");

    private void SendStructuredMessage(string command, object payload)
    {
        var json = JsonSerializer.Serialize(payload, SerializerOptions);
        SendMessage($"{command}:{json}");
    }

    private void SendMessage(string message)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
            client.Connect(5000);
            if (client.IsConnected)
            {
                byte[] messageBytes = Encoding.UTF8.GetBytes(message);
                client.Write(messageBytes, 0, messageBytes.Length);
                ConsoleLog.Success("Message sent to windower.");
            }
            else
            {
                ConsoleLog.Warning("Could not connect to the windower pipe.");
            }
        }
        catch (Exception ex)
        {
            ConsoleLog.Error($"Error sending message to windower: {ex.Message}");
        }
    }

    private readonly record struct VariantPayload(string SessionId, string VariantName, string Text);
}

