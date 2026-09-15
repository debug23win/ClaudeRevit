using System;

namespace ClaudeRevit.Services;

// Who is currently driving Revit over MCP.
//
// The protocol tells a server the CLIENT (initialize -> clientInfo: name + version) but never the
// MODEL behind it — there is no field for it. So the model is *asked*: the handshake instructions
// request one call to report_driving_model, and that answer is kept here.
//
// That answer is therefore SELF-REPORTED and not authoritative: a model can be wrong or vague about
// its own identity, and nothing stops a client from not calling the tool at all. Everything shown
// to the user is labelled accordingly — the client name, which does come from the protocol, is the
// trustworthy half.
public static class McpSession
{
    public static string? ClientName { get; private set; }
    public static string? ClientVersion { get; private set; }
    public static string? ReportedModel { get; private set; }
    public static DateTime? ConnectedUtc { get; private set; }

    public static event Action? Changed;

    // Called on each initialize. A new handshake is a new session, so any previously reported model
    // is cleared rather than carried over to a different client.
    public static void OnConnect(string? name, string? version)
    {
        ClientName = string.IsNullOrWhiteSpace(name) ? null : name!.Trim();
        ClientVersion = string.IsNullOrWhiteSpace(version) ? null : version!.Trim();
        ReportedModel = null;
        ConnectedUtc = DateTime.UtcNow;
        Raise();
    }

    public static void ReportModel(string? model)
    {
        ReportedModel = string.IsNullOrWhiteSpace(model) ? null : model!.Trim();
        Raise();
    }

    public static void Clear()
    {
        ClientName = ClientVersion = ReportedModel = null;
        ConnectedUtc = null;
        Raise();
    }

    // One line for the UI. Keeps the two facts distinct: the client is protocol-reported, the model
    // is the model's own claim.
    public static string Describe(bool russian = false)
    {
        if (ClientName == null)
            return russian ? "клиент не подключён" : "no client connected";

        var client = ClientName + (ClientVersion != null ? " " + ClientVersion : "");
        if (ReportedModel == null)
            return russian
                ? $"клиент: {client} · модель не сообщена"
                : $"client: {client} · model not reported";

        return russian
            ? $"клиент: {client} · модель: {ReportedModel} (со слов модели)"
            : $"client: {client} · model: {ReportedModel} (self-reported)";
    }

    private static void Raise()
    {
        try { Changed?.Invoke(); } catch { /* UI subscribers must never break the server */ }
    }
}
