# Organização de Dante.Application por feature (#203, AD-49)

A Application é organizada verticalmente por feature (Epic #202): uma pasta por
feature, com namespace `Dante.Application.<Feature>`. DTOs, ports e AppServices
ficam na feature que os usa; não existe projeto `Dante.Application.DTO`, e a
Application continua sem EF Core, Npgsql ou outro provider técnico.

## Regras gerais

- **um tipo público de topo por arquivo**, com o nome do tipo (`AcessoAoBrain.cs`,
  `IIndiceDeBusca.cs`). Não criar arquivos agregadores como `ContratosDe*.cs` nem
  declarar DTOs/ports dentro do arquivo do AppService;
- **o namespace é sempre o da feature**, inclusive dentro de subpastas. Subpasta
  organiza arquivos, não muda o contrato público nem exige `using` novo nos
  consumidores. `HexagonalArchitectureTests` recusa namespaces abaixo da feature;
- tipo usado por várias features fica na feature dona do conceito (`ProvenienciaDto`
  em `Conhecimentos`, `AcessoAoBrain` em `SegurancaDoBrain`). `Comum` guarda só a base
  genérica de aplicação (CRUD, `IRepository`, `IUnitOfWork`);
- nomes em PT-BR com os sufixos técnicos estabelecidos: `AppService`, `Repository`,
  `Dto`, `SearchDto` (AD-36, [base CRUD](crud.md)).

## Módulo simples e módulo complexo

Um módulo é **complexo** quando tem ports além do próprio repository (índices,
consultas, geradores, estado externo) ou casos de uso internos. Os demais são
**simples**. A diferença é só física:

| Elemento | Módulo simples | Módulo complexo |
| --- | --- | --- |
| AppService/fachada (`<Conceito>AppService`) e sua interface pública | raiz | raiz |
| Serviço público sem estado da feature (`ResolvedorDeIntencaoDoBrain`) | raiz | raiz |
| DTO, SearchDto e enum do contrato público | raiz | `Contratos/` |
| Repository port (`I<Entidade>Repository`) | raiz | `Portas/` |
| Outras ports implementadas pela Infrastructure e records trocados só com elas | — | `Portas/` |
| Mapping da feature | raiz | raiz |
| Validação de entrada | raiz | `Validacao/` |
| Caso de uso auxiliar interno | raiz | `CasosDeUso/` |

Subpastas só existem quando há arquivo para elas. Cada feature com mappings tem um
`<Entidade>Mapping` `internal static` na raiz, composto por
`Mapeamento/MapeamentosDaApplication` ([mappings](mapping.md), #204). Validators por
feature são definidos pela #205; regra de negócio continua no Domain ou no AppService.

Módulo simples (`Projetos`):

```text
Projetos/
├─ IProjetoAppService.cs
├─ IProjetoRepository.cs
├─ ProjetoAppService.cs
├─ ProjetoMapping.cs
├─ ProjetoDto.cs
└─ ProjetoSearchDto.cs
```

Módulo complexo (`BuscaDoBrain`), todos os tipos em `Dante.Application.BuscaDoBrain`:

```text
BuscaDoBrain/
├─ BuscaDoBrainAppService.cs
├─ Contratos/
│  ├─ BuscaDoBrainDto.cs
│  ├─ BuscaDoBrainSearchDto.cs
│  ├─ OrigemDoResultado.cs
│  └─ ResultadoDaBuscaDto.cs
└─ Portas/
   ├─ IGeradorDeEmbedding.cs
   ├─ IIndiceDeBusca.cs
   ├─ MatchDaBusca.cs
   └─ ModeloEmbedding.cs
```

## Classificação atual

| Complexos | Simples |
| --- | --- |
| `AuditoriaDoBrain`, `BuscaDoBrain`, `ConversaDoBrain`, `DocumentosFonte`, `QualidadeDoBrain` | `CapturaDeConhecimento`, `Conhecimentos`, `ConstrucaoDeContexto`, `ContextosDeTrabalho`, `EspacosDeConhecimento`, `Projetos`, `RelacoesDeConhecimento`, `SegurancaDoBrain` |

Quando um módulo simples ganhar uma port própria ou um caso de uso interno, ele passa
a complexo e seus contratos/ports vão para as subpastas no mesmo PR.

`Agentes`, `Anexos`, `Contextos` e `Uso` são legado extraído do Worker e mantêm a
organização existente até migração explícita (AD-36, AD-38). `Mapeamento` e `Comum`
são infraestrutura da própria Application.

## Validação de entrada e regra de negócio (#205, AD-52)

| Tipo de verificação | Onde fica | Exemplos |
| --- | --- | --- |
| **Validação de entrada**: só olha os valores recebidos | `<Conceito>Validator` da feature (raiz no módulo simples, `Validacao/` no complexo) ou o helper `Comum/ValidacaoDeEntrada` | `Guid.Empty` obrigatório, limite entre 1 e N, texto obrigatório/tamanho máximo, enum definido, formato aceito, combinação estrutural de campos |
| **Regra contextual**: depende de repository, estado persistido, identidade ou policy | AppService/caso de uso | espaço arquivado é somente leitura, projeto de outro espaço, usuário sem acesso, decisão confirmada no mesmo escopo |
| **Invariante**: vale para toda instância da entidade | Domain | revisão esperada desatualizada, consolidação não reduz sensibilidade, resolução de conflito exige decisão explícita |

Validators são classes `internal static`, sem estado nem dependências: recebem o
DTO/SearchDto ou os parâmetros e lançam `ArgumentException`/`ArgumentOutOfRangeException`
com mensagem e `ParamName` estáveis. Não acessam repository, unit of work, DbContext ou
Npgsql. O AppService chama o validator no mesmo ponto em que o check ficava, de modo
que a ordem entre validação de acesso e validação de entrada não muda. Normalização
(por exemplo `Trim` de filtros) continua no AppService.

`ValidacaoDeEntrada` concentra os checks repetidos entre features: `ExigirFaixa`
(limites, deslocamentos e profundidades), `ExigirId` e `ExigirEscopo` (espaço
obrigatório, projeto opcional mas nunca `Guid.Empty`). Check exclusivo de uma feature
fica no validator dela. Proteção de segredos (`ProtecaoDeSegredos`) é policy de
segurança e não é validator. `ValidacaoDeEntradaTests` testa os validators sem
repository nem banco. Não há FluentValidation: os checks atuais são poucos e diretos.
