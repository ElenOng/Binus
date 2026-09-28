using Microsoft.Extensions.Configuration;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Threading.Tasks;

namespace Binus.DataAccess;

public class UserRepository : IUserRepository
{
    private readonly string _connectionString;

    public UserRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection");
    }

    public async Task<string?> ValidateCredentialsAndGetDisplayNameAsync(string binusianId, string password)
    {
        // Join master_user with master_data_pribadi_pegawai to retrieve display name
        const string sql = @"SELECT mu.password, mdp.name
            FROM master_user mu
            LEFT JOIN master_data_pribadi_pegawai mdp ON mu.binusian_id = mdp.binusian_id
            WHERE mu.binusian_id = @id";

        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.VarChar, 50) { Value = binusianId });

        await conn.OpenAsync();
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;

        var dbPassword = reader.IsDBNull(0) ? null : reader.GetString(0);
        var displayName = reader.IsDBNull(1) ? null : reader.GetString(1);

        if (dbPassword == null)
            return null;

        return dbPassword == password ? displayName ?? binusianId : null;
    }
}
