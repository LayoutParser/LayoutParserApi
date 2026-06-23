using System.ComponentModel;
using System.Net.Http;
using ModelContextProtocol.Server;

namespace LayoutParserMcp.Tools;

/// <summary>
/// Tools de parsing — casam um layout XML com um documento posicional via a API.
/// </summary>
[McpServerToolType]
public static class ParseTools
{
    /// <summary>
    /// Parseia um documento posicional (TXT / MQSeries / IDOC) contra um layout XML,
    /// chamando POST /api/parse/upload. Recebe caminhos de arquivo locais.
    /// </summary>
    [McpServerTool(Name = "parse_document")]
    [Description("Parseia um documento (TXT/MQSeries/IDOC) contra um layout XML e retorna a estrutura parseada (JSON). " +
                 "Parses a positional document against an XML layout and returns the parsed structure as JSON.")]
    public static async Task<string> ParseDocumentAsync(
        IHttpClientFactory httpClientFactory,
        [Description("Caminho local do arquivo de layout (.xml).")] string layoutXmlPath,
        [Description("Caminho local do documento a parsear (.txt, .mq_series, .idoc).")] string documentPath,
        [Description("Nome do layout (opcional) — usado para aprendizado e override de detecção.")] string? layoutName = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(layoutXmlPath))
            return $"ERRO: arquivo de layout não encontrado: {layoutXmlPath}";
        if (!File.Exists(documentPath))
            return $"ERRO: documento não encontrado: {documentPath}";

        var client = httpClientFactory.CreateClient("api");

        using var form = new MultipartFormDataContent();

        var layoutBytes = await File.ReadAllBytesAsync(layoutXmlPath, cancellationToken);
        var layoutContent = new ByteArrayContent(layoutBytes);
        form.Add(layoutContent, "layoutFile", Path.GetFileName(layoutXmlPath));

        var docBytes = await File.ReadAllBytesAsync(documentPath, cancellationToken);
        var docContent = new ByteArrayContent(docBytes);
        form.Add(docContent, "txtFile", Path.GetFileName(documentPath));

        if (!string.IsNullOrWhiteSpace(layoutName))
            form.Add(new StringContent(layoutName), "layoutName");

        try
        {
            var response = await client.PostAsync("/api/parse/upload", form, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return response.IsSuccessStatusCode
                ? body
                : $"ERRO HTTP {(int)response.StatusCode}: {body}";
        }
        catch (Exception ex)
        {
            return $"ERRO ao chamar a API ({client.BaseAddress}): {ex.Message}";
        }
    }
}
