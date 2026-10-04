namespace DigitalBrain.Apps;

// Host-side surfaces of the App grain, deliberately outside the contracts assembly so scripts
// never see them. Adoption retires the package an installed replacement supersedes; the
// handover lets the adopting app enumerate the legacy app's storage owner keys.
public interface IAppAdoption : Orleans.IGrainWithStringKey
{
    Task Adopt(PackageId legacy);
}

public interface IAppStorageHandover : Orleans.IGrainWithStringKey
{
    Task<string[]> ReadStorageOwners();
}
