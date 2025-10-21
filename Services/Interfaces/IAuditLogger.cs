using LayoutParserApi.Models.Logging;

namespace LayoutParserApi.Services.Interfaces
{
    public interface IAuditLogger
    {
        void LogAudit(AuditLogEntry entry);
    }

    public interface ITechLogger
    {
        void LogTechnical(LogEntry entry);
    }

}
