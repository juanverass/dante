# Base CRUD da Application (AD-39, #171)

`EntidadeBase` (Domain) e `IRepository<TEntity>`, `IUnitOfWork`,
`ICrudBasicoAppService<TDto,TSearchDto,TEntity>` e
`CrudBasicoAppService<TDto,TSearchDto,TEntity>` (Application, `Dante.Application.Comum`)
eliminam o boilerplate de operações CRUD comuns. A base elimina boilerplate, não semântica:
invariantes continuam na entidade, consultas específicas no repository específico e
dependências próprias no construtor do AppService específico.

## Identidade

Toda entidade persistente herda `EntidadeBase`: `Guid Id` gerado pelo domínio, com setter
protegido. Não existe `EntidadeBase<TId>` nem parâmetro `TId` nas bases. DTO, mapping e
host não redefinem o Id: em `AtualizarAsync` ele vem do parâmetro, nunca do DTO, e o
mapping de criação usa o constructor/fábrica do domínio (AD-37).

## O que a base faz

| Operação | Comportamento |
| --- | --- |
| `ObterPorIdAsync` | carrega pelo repository e mapeia `TEntity → TDto`; null quando não existe |
| `PesquisarAsync` | chama `ConsultarAsync(filtro)` do AppService específico e mapeia cada entidade |
| `AdicionarAsync` | cria por `CriarEntidade` (mapping `TDto → TEntity` registrado), adiciona e salva |
| `AtualizarAsync` | carrega, chama `AplicarAlteracoes(entidade, dto)`, `Atualizar` e salva; null quando não existe |
| `RemoverAsync` | carrega, `Remover` e salva; false quando não existe |

Toda escrita é confirmada uma vez por `IUnitOfWork.SalvarAlteracoesAsync`; falha de
validação antes da escrita não salva nada. As operações são `virtual` para que o
AppService específico acrescente regras (autorização, escopo) sem reimplementar o resto.

O AppService específico implementa obrigatoriamente:

- `ConsultarAsync(TSearchDto, CancellationToken)`: valida filtros/limites do SearchDto e
  chama uma consulta do repository específico. SearchDto nunca vira consulta por mapping;
- `AplicarAlteracoes(TEntity, TDto)`: chama métodos do domínio. Nunca copia propriedades
  sobre a entidade (não há map-to-target, AD-37).

Os mappings `TEntity → TDto` e `TDto → TEntity` são registrados explicitamente em
`MapeamentosDaApplication`/`AddMapeamentos`, como define o [guia de mappings](mapping.md).
`CriarEntidade` e `ParaDto` podem ser sobrescritos quando a criação precisa de uma fábrica
com dependências do caso de uso.

## Nomenclatura dos consumidores

Nomes em PT-BR derivados da entidade, com os sufixos técnicos estabelecidos:

```csharp
public interface IProjetoRepository : IRepository<Projeto>
{
    Task<IReadOnlyList<Projeto>> ListarPorTrechoDoNomeAsync(string? trecho, int limite,
        CancellationToken cancellationToken = default);
}

public interface IProjetoAppService
    : ICrudBasicoAppService<ProjetoDto, ProjetoSearchDto, Projeto>
{
    Task<bool> ArquivarAsync(Guid id, CancellationToken cancellationToken = default);
}

public sealed class ProjetoAppService
    : CrudBasicoAppService<ProjetoDto, ProjetoSearchDto, Projeto>,
      IProjetoAppService
{
    public ProjetoAppService(IProjetoRepository projetos, IUnitOfWork unitOfWork,
        IMapsterTypeAdapter typeAdapter) : base(projetos, unitOfWork, typeAdapter) { ... }
}
```

Exemplo ilustrativo e simplificado: o `Projeto` real do Brain vive em `Dante.Domain.Projetos` e
`Dante.Application.Projetos` (#136, AD-42); os consumidores fictícios dos testes ficam em
`Dante.Tests.CrudDeExemplo`.
A implementação EF de `Repository<TEntity>` e de `IUnitOfWork` pertence à #168.

## Validação

`CrudBasicoAppServiceTests` cobre as operações da base sobre consumidores fictícios
(`tests/Dante.Tests/CrudDeExemplo.cs`): identidade gerada pelo domínio e mantida na
atualização, recusa antes de escrever, consulta específica com filtros validados, operação
própria do AppService específico, forma das bases (genéricas só na entidade, sem `TId`,
`EntidadeBase` com setter protegido e Domain sem Mapster) e a convenção de nomes
`I{Entidade}Repository`, `I{Entidade}AppService`, `{Entidade}AppService`, `{Entidade}Dto` e
`{Entidade}SearchDto`, verificada por reflexão na Application e nos exemplos.
