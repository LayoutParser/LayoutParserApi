namespace LayoutParserApi.Services.Interfaces
{
    /// <summary>
    /// Scanner de antivírus assíncrono (Slice 2 — spec §13). Único mecanismo disponível no host é o
    /// Windows Defender — implementação real dispara <c>MpCmdRun.exe</c>/<c>Start-MpScan</c> em
    /// processo externo. Nunca deve bloquear a resposta HTTP do upload: sempre chamado via
    /// fire-and-forget pelo serviço de orquestração.
    /// </summary>
    public interface IAntivirusScanner
    {
        /// <summary>
        /// Escaneia o arquivo em <paramref name="filePath"/> e retorna <c>true</c> se limpo,
        /// <c>false</c> se ameaça detectada. Retorna <c>null</c> quando o mecanismo está indisponível
        /// no ambiente (ex.: Defender não instalado/acessível) — nesse caso o chamador deve manter o
        /// artefato como <c>Pending</c> indefinidamente, sem travar, e logar warning (degrade,
        /// não fail-closed, porque o upload já foi aceito e o scan é best-effort).
        /// </summary>
        Task<bool?> ScanAsync(string filePath, CancellationToken cancellationToken);
    }
}
