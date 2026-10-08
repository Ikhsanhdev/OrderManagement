using Microsoft.EntityFrameworkCore;
using Npgsql;
namespace OrderManagement.Api.Infrastructure.Persistence;

public static class DbExceptionExtensions
{
    public static bool IsUniqueViolation(
        this Exception exception,
        string constraintName)
    {
        return exception is DbUpdateException
            && exception.InnerException is PostgresException postgresException
            && postgresException.SqlState == PostgresErrorCodes.UniqueViolation
            && postgresException.ConstraintName == constraintName;
    }
}
