using System.Threading.Tasks;

namespace Binus.DataAccess;

public interface IUserRepository
{
    // Validate credentials and return the display name from master_data_pribadi_pegawai when valid, otherwise null
    Task<string?> ValidateCredentialsAndGetDisplayNameAsync(string binusianId, string password);
}
