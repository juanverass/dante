namespace Dante.Tests;

// Testes que alteram o ambiente global não podem concorrer com hosts que leem a configuração.
[CollectionDefinition("AmbienteDosAgentes", DisableParallelization = true)]
public sealed class AmbienteDosAgentesCollection;
