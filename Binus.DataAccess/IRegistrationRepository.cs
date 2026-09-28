using System.Collections.Generic;
using System.Threading.Tasks;

namespace Binus.DataAccess;

public interface IRegistrationRepository
{
    Task<List<(int AnakKe, string Nama, System.DateTime? TanggalLahir)>> GetChildrenAsync(string binusianId);
    Task<List<(int ShiftId, string ShiftInfo, int Quota)>> GetShiftsAsync();
    Task<(int? MinimumAge, int? MaximumAge)?> GetBatchAgeRuleByShiftAsync(int shiftId);
    /// <summary>
    /// Save registration for a binusian.
    /// Return codes:
    /// 0 = success
    /// 1 = quota not sufficient or quota row missing
    /// 2 = one or more selected children are already registered for the selected shift by another user
    /// 3 = submitted data is identical to existing registration for this user (no changes)
    /// 4 = one or more selected children do not satisfy age rule for the selected shift
    /// </summary>
    Task<int> SaveRegistrationAsync(string binusianId, IEnumerable<int> anakKes, int shiftId);

    /// <summary>
    /// Return anak_ke values that are already registered for the given shift by other binusians.
    /// </summary>
    Task<List<int>> GetRegisteredAnakKesAsync(int shiftId, IEnumerable<int> anakKes, string excludeBinusianId);
}
