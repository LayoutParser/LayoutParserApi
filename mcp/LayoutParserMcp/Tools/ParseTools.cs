using System.ComponentModel;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Serilog.Context;

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
        ILogger<ParseToolsLog> logger,
        [Description("Caminho local do arquivo de layout (.xml).")] string layoutXmlPath,
        [Description("Caminho local do documento a parsear (.txt, .mq_series, .idoc).")] string documentPath,
        [Description("Nome do layout (opcional) — usado para aprendizado e override de detecção.")] string? layoutName = null,
        CancellationToken cancellationToken = default)
    {
        var correlationId = CorrelationContext.NewId();
        using var _ = LogContext.PushProperty("CorrelationId", correlationId);

        if (!File.Exists(layoutXmlPath))
        {
            logger.LogWarning("Tool parse_document: layout não encontrado em {LayoutXmlPath}", layoutXmlPath);
            return $"ERRO: arquivo de layout não encontrado: {layoutXmlPath}";
        }
        if (!File.Exists(documentPath))
        {
            logger.LogWarning("Tool parse_document: documento não encontrado em {DocumentPath}", documentPath);
            return $"ERRO: documento não encontrado: {documentPath}";
        }

        var client = httpClientFactory.CreateClient("api");
        client.DefaultRequestHeaders.TryAddWithoutValidation(CorrelationContext.HeaderName, correlationId);

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
            logger.LogInformation("Tool parse_document: {LayoutXmlPath} + {DocumentPath} (layoutName={LayoutName})",
                layoutXmlPath, documentPath, layoutName);
            var response = await client.PostAsync("/api/parse/upload", form, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                logger.LogWarning("Tool parse_document: HTTP {StatusCode} para {DocumentPath}", (int)response.StatusCode, documentPath);
            return response.IsSuccessStatusCode
                ? body
                : $"ERRO HTTP {(int)response.StatusCode}: {body}";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Tool parse_document: falha ao chamar a API ({BaseAddress})", client.BaseAddress);
            return $"ERRO ao chamar a API ({client.BaseAddress}): {ex.Message}";
        }
    }
}
