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
        const string sql = @"SELECT s.shift_id, s.shift_info, s.quota
                             FROM master_shift_quota s
                             INNER JOIN master_batch b ON s.batch_id = b.batch_id
                             WHERE b.cutoffdate IS NULL OR CAST(b.cutoffdate AS date) >= CAST(GETDATE() AS date)
                             ORDER BY s.batch_id, s.shift_id";

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

    public async Task<(int? MinimumAge, int? MaximumAge)?> GetBatchAgeRuleByShiftAsync(int shiftId)
    {
        const string sql = @"SELECT TOP 1 b.minimum_age, b.maximum_age
                             FROM master_shift_quota s
                             INNER JOIN master_batch b ON s.batch_id = b.batch_id
                             WHERE s.shift_id = @shiftId";

        await using var conn = new SqlConnection(_connectionString);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@shiftId", SqlDbType.Int) { Value = shiftId });

        await conn.OpenAsync();
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        var minimumAge = reader.IsDBNull(0) ? (int?)null : reader.GetInt32(0);
        var maximumAge = reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1);

        return (minimumAge, maximumAge);
    }

    public async Task<int> SaveRegistrationAsync(string binusianId, IEnumerable<int> anakKes, int shiftId)
    {
        var anakList = anakKes?.ToList() ?? new List<int>();
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();

        using var tx = conn.BeginTransaction();
        try
        {
            if (anakList.Count > 0)
            {
                var ageRule = await GetBatchAgeRuleByShiftAsync(shiftId);
                if (ageRule == null || !ageRule.Value.MinimumAge.HasValue || !ageRule.Value.MaximumAge.HasValue)
                {
                    tx.Rollback();
                    return 4;
                }

                var inParams = new List<string>();
                for (int i = 0; i < anakList.Count; i++)
                {
                    inParams.Add("@a" + i);
                }

                var childrenSql = $"SELECT anak_ke, tanggal_lahir FROM master_data_anak_pegawai WHERE binusian_id = @id AND anak_ke IN ({string.Join(',', inParams)})";
                var minAge = ageRule.Value.MinimumAge.Value;
                var maxAge = ageRule.Value.MaximumAge.Value;
                var ageReferenceDate = System.DateTime.Today;
                var matched = new HashSet<int>();

                await using (var childCmd = new SqlCommand(childrenSql, conn, tx))
                {
                    childCmd.Parameters.Add(new SqlParameter("@id", SqlDbType.VarChar, 50) { Value = binusianId });
                    for (int i = 0; i < anakList.Count; i++)
                    {
                        childCmd.Parameters.Add(new SqlParameter("@a" + i, SqlDbType.Int) { Value = anakList[i] });
                    }

                    await using var childReader = await childCmd.ExecuteReaderAsync();
                    while (await childReader.ReadAsync())
                    {
                        var anakKe = childReader.GetInt32(0);
                        matched.Add(anakKe);

                        if (childReader.IsDBNull(1))
                        {
                            tx.Rollback();
                            return 4;
                        }

                        var dob = childReader.GetDateTime(1);
                        var age = ageReferenceDate.Year - dob.Year;
                        if (dob.Date > ageReferenceDate.AddYears(-age)) age--;

                        if (age < minAge || age > maxAge)
                        {
                            tx.Rollback();
                            return 4;
                        }
                    }
                }

                if (matched.Count != anakList.Count)
                {
                    tx.Rollback();
                    return 4;
                }

                // Check if any selected child is already registered for this shift by a different binusian
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

            const string targetBatchSql = "SELECT batch_id FROM master_shift_quota WHERE shift_id = @shiftId";
            int targetBatchId = 0;
            await using (var bCmd = new SqlCommand(targetBatchSql, conn, tx))
            {
                bCmd.Parameters.Add(new SqlParameter("@shiftId", SqlDbType.Int) { Value = shiftId });
                var bObj = await bCmd.ExecuteScalarAsync();
                if (bObj == null || bObj == DBNull.Value)
                {
                    tx.Rollback();
                    return 1;
                }
                targetBatchId = (int)bObj;
            }

            // Load existing registrations for this user (anak_ke + shift_id + batch_id)
            var existingRegs = new List<(int AnakKe, int ShiftId, int BatchId)>();
            const string listSql = @"SELECT tr.anak_ke, tr.shift_id, msq.batch_id
                                     FROM transaction_registration tr
                                     INNER JOIN master_shift_quota msq ON tr.shift_id = msq.shift_id
                                     WHERE tr.binusian_id = @id";
            await using (var listCmd = new SqlCommand(listSql, conn, tx))
            {
                listCmd.Parameters.Add(new SqlParameter("@id", SqlDbType.VarChar, 50) { Value = binusianId });
                await using var lr = await listCmd.ExecuteReaderAsync();
                while (await lr.ReadAsync())
                {
                    var anak = lr.IsDBNull(0) ? 0 : lr.GetInt32(0);
                    var sh = lr.IsDBNull(1) ? 0 : lr.GetInt32(1);
                    var batch = lr.IsDBNull(2) ? 0 : lr.GetInt32(2);
                    existingRegs.Add((anak, sh, batch));
                }
            }

            var existingForShift = existingRegs.Where(r => r.ShiftId == shiftId).Select(r => r.AnakKe).ToHashSet();
            var selectedForShift = anakList.ToHashSet();

            // Quotas decrease when new registrations are created.
            // For same-batch move across shifts, old shift quota is restored.

            // Same-batch move rule:
            // - If selected child already exists in another shift under the same batch,
            //   remove old shift row and keep only the new shift row.
            // - If child exists in a different batch, keep existing row and insert new row.
            var sameBatchOtherShiftForSelected = existingRegs
                .Where(r => r.BatchId == targetBatchId && r.ShiftId != shiftId && selectedForShift.Contains(r.AnakKe))
                .Select(r => (r.AnakKe, r.ShiftId))
                .ToList();

            var toDelete = existingForShift
                .Where(a => !selectedForShift.Contains(a))
                .Select(a => (AnakKe: a, ShiftId: shiftId))
                .Concat(sameBatchOtherShiftForSelected)
                .Distinct()
                .ToList();

            var toInsert = selectedForShift.Where(a => !existingForShift.Contains(a)).ToList();

            if (toDelete.Count == 0 && toInsert.Count == 0)
            {
                tx.Rollback();
                return 3; // no changes - data already exists
            }

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

            // Check quota against the number of new registrations only
            if (currentQuota < toInsert.Count)
            {
                tx.Rollback();
                return 1; // not enough slots for the new registrations
            }

            if (toDelete.Count > 0)
            {
                const string deleteSql = "DELETE FROM transaction_registration WHERE binusian_id = @id AND shift_id = @shiftId AND anak_ke = @anakKe";
                await using var delCmd = new SqlCommand(deleteSql, conn, tx);
                delCmd.Parameters.Add(new SqlParameter("@id", SqlDbType.VarChar, 50) { Value = binusianId });
                var pShift = (SqlParameter)delCmd.Parameters.Add(new SqlParameter("@shiftId", SqlDbType.Int) { Value = 0 });
                var pAnak = (SqlParameter)delCmd.Parameters.Add(new SqlParameter("@anakKe", SqlDbType.Int) { Value = 0 });

                foreach (var row in toDelete)
                {
                    pShift.Value = row.ShiftId;
                    pAnak.Value = row.AnakKe;
                    await delCmd.ExecuteNonQueryAsync();
                }
            }

            if (sameBatchOtherShiftForSelected.Count > 0)
            {
                const string incSql = "UPDATE master_shift_quota SET quota = quota + @cnt WHERE shift_id = @shiftId";
                await using var incCmd = new SqlCommand(incSql, conn, tx);
                var pCnt = (SqlParameter)incCmd.Parameters.Add(new SqlParameter("@cnt", SqlDbType.Int) { Value = 0 });
                var pShift = (SqlParameter)incCmd.Parameters.Add(new SqlParameter("@shiftId", SqlDbType.Int) { Value = 0 });

                foreach (var grp in sameBatchOtherShiftForSelected.GroupBy(x => x.ShiftId))
                {
                    pCnt.Value = grp.Count();
                    pShift.Value = grp.Key;
                    await incCmd.ExecuteNonQueryAsync();
                }
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
