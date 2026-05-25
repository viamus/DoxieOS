namespace Viamus.Doxie.Orchestrator.Context;

public interface ILibraryStore
{
    IReadOnlyList<Library> ListAll();

    Library? GetById(string id);
}
