using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dante.Application.ConstrucaoDeContexto;
using Dante.Application.SegurancaDoBrain;
using Dante.Domain.CapturaDeConhecimento;
using Dante.Domain.Conhecimentos;
using Dante.Domain.ContextosDeTrabalho;
using Dante.Domain.DocumentosFonte;
using Dante.Domain.EspacosDeConhecimento;
using Dante.Domain.Projetos;
using Dante.Domain.RelacoesDeConhecimento;
using Dante.Infrastructure.Data;
using Dante.Infrastructure.SegurancaDoBrain;
using Dante.Worker.Telegram;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dante.Tests;

// Restauração, só para testes, do estado do Brain exportado por um E2E da #147 (resultado.json → estadoAntesDeB).
// Fidelidade não é presumida: o estado restaurado é reexportado pela mesma serialização do harness e precisa ser
// byte a byte igual ao export; o pacote é reconstruído pela mesma chamada de produção da sessão B do E2E.
// Este arquivo compila também em 97bd98d (pré-#289): a prova histórica roda o mesmo código naquele commit.

// Execução de retomada lida só depois de o manifesto conferir integralmente com o disco.
internal sealed record EvidenciaDeRetomada147(string Diretorio, string Id, int Repeticao, string EstadoAntesDeB, string ConsultaB,
    byte[] TextoDoPacote, byte[] PacoteJson, byte[] EntradaB, string IdDoPacote)
{
    internal const string NomeDoEspaco = "Desenvolvimento";
    internal const string NomeDoProjeto = "D.A.N.T.E.";
    // Parâmetros da sessão B no E2E (Issue147BrainIndependenteTests): mensagem 300, sessão "147B", Telegram 42/55.
    internal const long MensagemB = 300;
    internal const string SessaoB = "147B";

    internal static EvidenciaDeRetomada147 Ler(string diretorio)
    {
        var verificacao = ExecucaoDeMedicao147.Verificar(diretorio);
        if (verificacao.Divergencias.Count > 0)
            throw new InvalidDataException("Evidência não íntegra: " + string.Join("; ", verificacao.Divergencias));
        var manifesto = ExecucaoDeMedicao147.LerManifesto(diretorio);
        if (manifesto.Estado != "completo") throw new InvalidDataException("Execução não está completa: " + manifesto.Estado);
        if (!manifesto.Cenario.StartsWith("retomada", StringComparison.Ordinal)) throw new InvalidDataException("Execução não é de retomada: " + manifesto.Cenario);
        byte[] Arquivo(string nome) => File.ReadAllBytes(Path.Combine(diretorio, nome));
        using var resultado = JsonDocument.Parse(Arquivo("resultado.json"));
        using var pacote = JsonDocument.Parse(Arquivo("pacote.json"));
        return new(diretorio, manifesto.Id, manifesto.Repeticao, resultado.RootElement.GetProperty("estadoAntesDeB").GetString()!,
            resultado.RootElement.GetProperty("consultaB").GetString()!, Arquivo("pacote-texto-para-injecao.txt"), Arquivo("pacote.json"),
            Arquivo("entrada-b.txt"), pacote.RootElement.GetProperty("id").GetString()!);
    }
}

internal sealed record PacoteReproduzido147(byte[] TextoDoPacote, byte[] EntradaB, byte[] PacoteJson, string IdDoPacote, PacoteDeContextoDto Pacote)
{
    internal static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    // pacote.json com o único valor aleatório da construção (Id do pacote, Guid.NewGuid) trocado pelo do pacote comparado.
    // A troca é textual e exata: qualquer outra diferença de bytes continua visível.
    internal byte[] PacoteJsonComId(string id)
    {
        var texto = Encoding.UTF8.GetString(PacoteJson);
        var campo = $"\"id\": \"{IdDoPacote}\"";
        if (Ocorrencias(texto, IdDoPacote) != 1 || Ocorrencias(texto, campo) != 1)
            throw new InvalidDataException("Id do pacote não é único no JSON reproduzido.");
        return Encoding.UTF8.GetBytes(texto.Replace(campo, $"\"id\": \"{id}\"", StringComparison.Ordinal));
    }
    internal static int Ocorrencias(string texto, string valor)
    {
        var n = 0; for (var i = texto.IndexOf(valor, StringComparison.Ordinal); i >= 0; i = texto.IndexOf(valor, i + valor.Length, StringComparison.Ordinal)) n++;
        return n;
    }
}

internal static class RestauracaoDoBrain147
{
    private static readonly JsonSerializerOptions Indentado = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions Evidencia = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly string[] Colecoes = ["fontes", "conhecimentos", "candidatos", "vinculos", "relacoes", "snapshots"];

    // Mesma serialização de EstadoPersistidoAsync do harness E2E (opções, ordem por Id e coleções).
    internal static async Task<string> EstadoPersistidoAsync(DanteDbContext db, CancellationToken ct) =>
        JsonSerializer.Serialize(new
        {
            fontes = (await db.Set<DocumentoFonte>().AsNoTracking().ToListAsync(ct)).OrderBy(x => x.Id),
            conhecimentos = (await db.Conhecimentos.AsNoTracking().ToListAsync(ct)).OrderBy(x => x.Id),
            candidatos = (await db.CandidatosDeConhecimento.AsNoTracking().ToListAsync(ct)).OrderBy(x => x.Id),
            vinculos = (await db.Set<VinculoEntreCandidatos>().AsNoTracking().ToListAsync(ct)).OrderBy(x => x.Id),
            relacoes = (await db.RelacoesDeConhecimento.AsNoTracking().ToListAsync(ct)).OrderBy(x => x.Id),
            snapshots = (await db.Set<ContextoDeTrabalho>().AsNoTracking().ToListAsync(ct)).OrderBy(x => x.Id)
        }, Indentado);

    // Banco descartável recém-migrado e vazio. Espaço e projeto não fazem parte do export: são recriados com os Ids
    // exportados, o proprietário derivado da mesma identidade Telegram (42) e os nomes que o E2E usou.
    internal static async Task RestaurarAsync(string connectionString, string estadoAntesDeB, CancellationToken ct)
    {
        using var documento = JsonDocument.Parse(estadoAntesDeB);
        var raiz = documento.RootElement;
        var chaves = raiz.EnumerateObject().Select(x => x.Name).ToArray();
        if (!chaves.SequenceEqual(Colecoes)) throw new InvalidDataException("Coleções do export divergem: " + string.Join(",", chaves));
        var registros = Colecoes.SelectMany(c => raiz.GetProperty(c).EnumerateArray()).ToArray();
        var espacos = registros.Select(x => x.GetProperty("IdEspacoDeConhecimento").GetGuid()).Distinct().ToArray();
        var projetos = registros.Select(x => x.GetProperty("IdProjeto").ValueKind == JsonValueKind.Null ? (Guid?)null : x.GetProperty("IdProjeto").GetGuid()).Distinct().ToArray();
        if (espacos.Length != 1 || projetos.Length != 1 || projetos[0] is null) throw new InvalidDataException("O export precisa ter exatamente um espaço e um projeto.");
        var usuario = Identidade().IdUsuario;
        foreach (var r in raiz.GetProperty("fontes").EnumerateArray().Concat(raiz.GetProperty("snapshots").EnumerateArray()))
            if (r.GetProperty("IdResponsavel").GetGuid() != usuario) throw new InvalidDataException("Responsável do export difere da identidade de teste.");

        await using (var db = Banco.Contexto(connectionString))
        {
            if (await db.EspacosDeConhecimento.AnyAsync(ct) || await db.Conhecimentos.AnyAsync(ct) || await db.Set<DocumentoFonte>().AnyAsync(ct))
                throw new InvalidOperationException("Restauração exige banco vazio.");
            var espaco = new EspacoDeConhecimento(usuario, EvidenciaDeRetomada147.NomeDoEspaco);
            db.Entry(espaco).Property(x => x.Id).CurrentValue = espacos[0];
            var projeto = new Projeto(espacos[0], EvidenciaDeRetomada147.NomeDoProjeto);
            db.Entry(projeto).Property(x => x.Id).CurrentValue = projetos[0]!.Value;
            db.Add(espaco); await db.SaveChangesAsync(ct);
            db.Add(projeto); await db.SaveChangesAsync(ct);
        }
        // Ordem de dependência das FKs; dentro de cada tabela, a ordem do export. Uma linha por SaveChanges para
        // que a ordem física de inserção seja a escolhida aqui, não a do batch do EF.
        var tipos = new (string Colecao, Type Tipo)[]
        {
            ("fontes", typeof(DocumentoFonte)), ("conhecimentos", typeof(Conhecimento)), ("candidatos", typeof(CandidatoDeConhecimento)),
            ("relacoes", typeof(RelacaoDeConhecimento)), ("vinculos", typeof(VinculoEntreCandidatos)), ("snapshots", typeof(ContextoDeTrabalho))
        };
        foreach (var (colecao, tipo) in tipos)
            foreach (var registro in raiz.GetProperty(colecao).EnumerateArray())
            {
                await using var db = Banco.Contexto(connectionString);
                Materializar(db, tipo, registro);
                await db.SaveChangesAsync(ct);
            }
        await using (var db = Banco.Contexto(connectionString))
        {
            var reexportado = await EstadoPersistidoAsync(db, ct);
            if (reexportado != estadoAntesDeB) throw new InvalidDataException("Estado restaurado diverge do export: " + PrimeiraDiferenca(reexportado, estadoAntesDeB));
        }
    }

    // Cada propriedade mapeada pelo EF (inclusive campos privados como historico) recebe o valor exportado de mesmo nome.
    // Propriedade mapeada sem correspondente no export é falha: nada é preenchido por suposição. O token xmin é do banco.
    private static void Materializar(DanteDbContext db, Type tipo, JsonElement registro)
    {
        var entidade = db.Model.FindEntityType(tipo) ?? throw new InvalidOperationException("Tipo não mapeado: " + tipo.Name);
        var instancia = RuntimeHelpers.GetUninitializedObject(tipo);
        var entrada = db.Entry(instancia);
        var nomes = registro.EnumerateObject().Select(x => x.Name).ToArray();
        foreach (var propriedade in entidade.GetProperties())
        {
            if (propriedade.Name == "Versao") continue;
            var nome = nomes.SingleOrDefault(x => string.Equals(x, propriedade.Name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"{tipo.Name}.{propriedade.Name} ausente do export.");
            entrada.Property(propriedade.Name).CurrentValue = registro.GetProperty(nome).Deserialize(propriedade.ClrType);
        }
        entrada.State = EntityState.Added;
    }

    internal static IdentidadeDoBrain Identidade() =>
        new IdentidadeTelegramDoBrain(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Telegram:AllowedUserIds"] = "42,7" }).Build()).Resolver(42);

    // Mesma configuração do E2E (BrainMcpTests.CriarApp) e mesma chamada da sessão B: seleção por nome do espaço e do
    // projeto restaurados, PrepararAsync com a consulta exportada, mensagem 300 e sessão 147B (bootstrap).
    internal static async Task<PacoteReproduzido147> ReproduzirAsync(string connectionString, string consultaB, CancellationToken ct)
    {
        using var app = BrainMcpTests.CriarApp(connectionString);
        var brain = app.GetRequiredService<TelegramBrain>();
        var espaco = "usar espaço " + EvidenciaDeRetomada147.NomeDoEspaco;
        var projeto = "usar projeto " + EvidenciaDeRetomada147.NomeDoProjeto;
        var r1 = await brain.AtenderAsync(BrainMcpTests.Mensagem(espaco), espaco, null, ct);
        var r2 = await brain.AtenderAsync(BrainMcpTests.Mensagem(projeto), projeto, null, ct);
        if (r1?.Contains("selecionado") != true || r2?.Contains("selecionado") != true) throw new InvalidOperationException("Seleção do escopo restaurado falhou.");
        var contexto = await brain.PrepararAsync(BrainMcpTests.Mensagem(consultaB, EvidenciaDeRetomada147.MensagemB), EvidenciaDeRetomada147.SessaoB, consultaB, ct)
            ?? throw new InvalidOperationException("Nenhum pacote construído sobre o estado restaurado.");
        var utf8 = new UTF8Encoding(false);
        return new(utf8.GetBytes(contexto.Pacote.TextoParaInjecao), utf8.GetBytes(contexto.Texto),
            JsonSerializer.SerializeToUtf8Bytes(contexto.Pacote, Evidencia), contexto.Pacote.Id.ToString(), contexto.Pacote);
    }

    // Formato pré-#289 não tem campos nomeados no snapshot; é ele que precisa reproduzir os bytes históricos.
    internal static bool FormatoHistorico => typeof(ItemDeContextoDto).GetProperty("CamposDoSnapshot") is null;

    internal static string PrimeiraDiferenca(string a, string b)
    {
        var i = 0; while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        string Trecho(string s) => s.Substring(Math.Max(0, i - 60), Math.Min(s.Length - Math.Max(0, i - 60), 160));
        return $"posição {i} (tamanhos {a.Length}/{b.Length}); obtido «{Trecho(a)}» esperado «{Trecho(b)}»";
    }
}
