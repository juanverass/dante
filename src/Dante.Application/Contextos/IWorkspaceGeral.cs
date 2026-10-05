namespace Dante.Application.Contextos;

// Porta do workspace isolado do General Mode (AD-09). O adapter garante caminho absoluto e já existente.
public interface IWorkspaceGeral
{
    string Caminho { get; }
}
