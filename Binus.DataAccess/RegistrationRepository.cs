using Microsoft.Extensions.Configuration;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;

namespace Binus.DataAccess;

public class RegistrationRepository : IRegistrationRepository
{
    private readonly string _connectionString;

    public RegistrationRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection");
    }

    public async Task<List<(int AnakKe, string Nama, System.DateTime? TanggalLahir)>> GetChildrenAsync(string binusianId)
    {
        var list = new List<(int, string, System.DateTime?)>();
        const string sql = "SELECT anak_ke, nama, tanggal_lahir FROM master_data_anak_pegawai WHERE binusian_id = @id ORDER BY anak_ke";

        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@id", SqlDbType.VarChar, 50) { Value = binusianId });

        await conn.OpenAsync();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var anakKe = reader.GetInt32(0);
            var nama = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            System.DateTime? tanggal = null;
            if (!reader.IsDBNull(2)) tanggal = reader.GetDateTime(2);
            list.Add((anakKe, nama, tanggal));
        }

        return list;
    }

    public async Task<List<int>> GetRegisteredAnakKesAsync(int shiftId, IEnumerable<int> anakKes, string excludeBinusianId)
    {
        var result = new List<int>();
        var list = (anakKes ?? Enumerable.Empty<int>()).ToList();
        if (!list.Any()) return result;

        // build parameter list
        var inParams = new List<string>();
        for (int i = 0; i < list.Count; i++) inParams.Add("@a" + i);

        var sql = $"SELECT anak_ke FROM transaction_registration WHERE shift_id = @shiftId AND anak_ke IN ({string.Join(',', inParams)}) AND (binusian_id <> @exclude)";

        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@shiftId", SqlDbType.Int) { Value = shiftId });
        cmd.Parameters.Add(new SqlParameter("@exclude", SqlDbType.VarChar, 50) { Value = excludeBinusianId ?? string.Empty });
        for (int i = 0; i < list.Count; i++) cmd.Parameters.Add(new SqlParameter("@a" + i, SqlDbType.Int) { Value = list[i] });

        await conn.OpenAsync();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetInt32(0));
        }

        return result;
    }

    public async Task<List<(int ShiftId, string ShiftInfo, int Quota)>> GetShiftsAsync()
    {
        var list = new List<(int, string, int)>();
        const string sql = "SELECT shift_id, shift_info, quota FROM master_shift_quota ORDER BY shift_id";

        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, conn);
        await conn.OpenAsync();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var id = reader.GetInt32(0);
            var info = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            var quota = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
            list.Add((id, info, quota));
        }

        return list;
    }

    public async Task<int> SaveRegistrationAsync(string binusianId, IEnumerable<int> anakKes, int shiftId)
    {
        var anakList = anakKes?.ToList() ?? new List<int>();
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();

        using var tx = conn.BeginTransaction();
        try
        {
            // Check if any selected child is already registered for this shift by a different binusian
            if (anakList.Count > 0)
            {
                // build parameter list for anak_ke
                var inParams = new List<string>();
                for (int i = 0; i < anakList.Count; i++)
                {
                    inParams.Add("@a" + i);
                }
                var existsSql = $"SELECT TOP 1 binusian_id FROM transaction_registration WHERE shift_id = @shiftId AND anak_ke IN ({string.Join(',', inParams)})";
                await using (var exCmd = new SqlCommand(existsSql, conn, tx))
                {
                    exCmd.Parameters.Add(new SqlParameter("@shiftId", SqlDbType.Int) { Value = shiftId });
                    for (int i = 0; i < anakList.Count; i++)
                    {
                        exCmd.Parameters.Add(new SqlParameter("@a" + i, SqlDbType.Int) { Value = anakList[i] });
                    }

                    var found = await exCmd.ExecuteScalarAsync();
                    if (found != null && found != DBNull.Value)
                    {
                        var foundBinusian = (string)found;
                        if (!string.Equals(foundBinusian, binusianId, System.StringComparison.OrdinalIgnoreCase))
                        {
                            tx.Rollback();
                            return 2; // child already registered for this shift by another user
                        }
                    }
                }
            }

            // Get existing registrations for this user
            const string existingSql = "SELECT shift_id, COUNT(*) as cnt FROM transaction_registration WHERE binusian_id = @id GROUP BY shift_id";
            int prevShiftId = 0;
            int prevCount = 0;
            await using (var exCmd = new SqlCommand(existingSql, conn, tx))
            {
                exCmd.Parameters.Add(new SqlParameter("@id", SqlDbType.VarChar, 50) { Value = binusianId });
                await using var r = await exCmd.ExecuteReaderAsync();
                if (await r.ReadAsync())
                {
                    prevShiftId = r.IsDBNull(0) ? 0 : r.GetInt32(0);
                    prevCount = r.IsDBNull(1) ? 0 : r.GetInt32(1);
                }
            }

            // Load existing registrations for this user (anak_ke + shift_id)
            var existingRegs = new List<(int AnakKe, int ShiftId)>();
            const string listSql = "SELECT anak_ke, shift_id FROM transaction_registration WHERE binusian_id = @id";
            await using (var listCmd = new SqlCommand(listSql, conn, tx))
            {
                listCmd.Parameters.Add(new SqlParameter("@id", SqlDbType.VarChar, 50) { Value = binusianId });
                await using var lr = await listCmd.ExecuteReaderAsync();
                while (await lr.ReadAsync())
                {
                    var anak = lr.IsDBNull(0) ? 0 : lr.GetInt32(0);
                    var sh = lr.IsDBNull(1) ? 0 : lr.GetInt32(1);
                    existingRegs.Add((anak, sh));
                }
            }

            // If all selected children are already registered for the requested shift, do nothing
            var existingForShift = existingRegs.Where(r => r.ShiftId == shiftId).Select(r => r.AnakKe).ToHashSet();
            if (anakList.All(a => existingForShift.Contains(a)))
            {
                tx.Rollback();
                return 3; // no changes - data already exists
            }

            // NOTE: We intentionally DO NOT restore quota for previous registrations.
            // Quotas only decrease when new registrations are created. This avoids re-increasing
            // quotas when users edit their registration; quota counts are treated as a running
            // consumption metric.

            // Check current quota
            const string quotaSql = "SELECT quota FROM master_shift_quota WHERE shift_id = @shiftId";
            int currentQuota = 0;
            await using (var qCmd = new SqlCommand(quotaSql, conn, tx))
            {
                qCmd.Parameters.Add(new SqlParameter("@shiftId", SqlDbType.Int) { Value = shiftId });
                var qObj = await qCmd.ExecuteScalarAsync();
                if (qObj == null || qObj == DBNull.Value)
                {
                    tx.Rollback();
                    return 1; // treat missing quota as insufficient
                }
                currentQuota = (int)qObj;
            }

            if (currentQuota < anakList.Count)
            {
                tx.Rollback();
                return 1; // not enough slots
            }


            // Determine which children are new registrations for the requested shift
            var toInsert = anakList.Where(a => !existingForShift.Contains(a)).ToList();

            // Check quota against the number of new registrations only
            if (currentQuota < toInsert.Count)
            {
                tx.Rollback();
                return 1; // not enough slots for the new registrations
            }

            if (toInsert.Count > 0)
            {
                const string insertSql = "INSERT INTO transaction_registration (binusian_id, anak_ke, shift_id) VALUES (@id, @anakKe, @shiftId)";
                await using (var insCmd = new SqlCommand(insertSql, conn, tx))
                {
                    insCmd.Parameters.Add(new SqlParameter("@id", SqlDbType.VarChar, 50) { Value = binusianId });
                    var pAnak = (SqlParameter)insCmd.Parameters.Add(new SqlParameter("@anakKe", SqlDbType.Int) { Value = 0 });
                    var pShift = (SqlParameter)insCmd.Parameters.Add(new SqlParameter("@shiftId", SqlDbType.Int) { Value = shiftId });

                    foreach (var anak in toInsert)
                    {
                        pAnak.Value = anak;
                        pShift.Value = shiftId;
                        await insCmd.ExecuteNonQueryAsync();
                    }
                }

                // Decrease quota by number of new children registered
                const string decSql = "UPDATE master_shift_quota SET quota = quota - @cnt WHERE shift_id = @shiftId";
                await using (var decCmd = new SqlCommand(decSql, conn, tx))
                {
                    decCmd.Parameters.Add(new SqlParameter("@cnt", SqlDbType.Int) { Value = toInsert.Count });
                    decCmd.Parameters.Add(new SqlParameter("@shiftId", SqlDbType.Int) { Value = shiftId });
                    await decCmd.ExecuteNonQueryAsync();
                }
            }

            tx.Commit();
            return 0;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }
}
