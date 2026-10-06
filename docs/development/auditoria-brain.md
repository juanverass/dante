# Inspeção e exportação do Brain

InspecaoDoBrainAppService permite listar espaços autorizados, inspecionar projetos,
conhecimento, relações e fontes brutas e exportar Markdown + JSON. Identidade scoped
é obrigatória. Auditoria explicitamente selecionada de um espaço inclui seus projetos;
selecionar projeto restringe todos os dados a ele. Essa autorização de leitura não
altera o escopo exato das operações funcionais. Spaces arquivados seguem auditáveis.

Inspeção tem filtros tipo/status/origem/tag, página até 1000 e offset até 10000. Origem
é substring case-insensitive da proveniência atual; tag é igualdade case-insensitive.
IncluirInativos é explícito, permite auditar itens inativos/substituídos/fora da validade.
Relações da página só contêm endpoints presentes na própria página. TemMais indica
continuação inclusive para fontes/relações. Não há listagem global de usuários/tenants.

Exportação completa, em snapshot repeatable-read, tem formato versionado independente
de agente. Preserva IDs, escopos, tipos, status, validade, confiança, histórico autorizado,
proveniência, direções e IDs de relações, fontes brutas e hashes. Exportação omite
inativos/substituídos/fora da validade e fontes removidas. Secret nunca é exportado,
inclusive em revisões históricas, mesmo com permissão de leitura manual. Confidential
exige permissão explícita. Campos textuais passam por redaction; histórico e provas de
relações com classificação anterior mais restrita não revelam conteúdo.

Limites: 1000 conhecimentos, 1000 fontes, 10000 relações e 10 MB JSON. Exceder recusa a
exportação, sem entregar arquivo parcial; selecione um projeto menor. Markdown usa
blocos literais com delimitador maior que qualquer sequência presente no conteúdo.
O JSON é o formato completo para auditoria/migração, não um backup operacional do banco.

Fallback administrativo local (não autenticação web):

```bash
dotnet run --project src/Dante.Worker -- --brain inspect USUARIO SPACE PROJETO
dotnet run --project src/Dante.Worker -- --brain export USUARIO SPACE PROJETO /tmp/brain-export
```

Use `-` como projeto para exportar o espaço inteiro, inclusive conhecimento sem projeto.
Os IDs são seleção explícita do administrador local; nunca são credenciais aceitas de
mensagens. O comando não habilita Confidential/Secret. Export cria `.json` e `.md`,
com permissão 0600 no Unix; não sobrescreve arquivos e remove apenas arquivos próprios
se a criação do par falhar. Inspecionar/exportar não altera nenhum dado canônico.
