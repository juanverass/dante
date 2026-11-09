using System.Data.Common;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dante.Application.Comum;

namespace Dante.Worker.Brain;

// Conjunto fechado de categorias de erro do canal MCP (#251). Os códigos são estáveis e documentados em brain-mcp.md.
public enum CategoriaDeErroMcp { EntradaInvalida, RevisaoDesatualizada, NaoEncontradoOuNaoAutorizado, EscopoMudou, Indisponivel }

// Recusa já classificada pelo adapter. O detalhe é texto fixo escrito aqui, nunca mensagem de exceção,
// identificador ou conteúdo de outro escopo; NaoEncontradoOuNaoAutorizado nunca leva detalhe.
public sealed class ErroDoBrainMcp(CategoriaDeErroMcp categoria, string? detalhe = null) : Exception(ErrosDoBrainMcp.Codigo(categoria))
{
    public CategoriaDeErroMcp Categoria { get; } = categoria;
    public string? Detalhe { get; } = categoria == CategoriaDeErroMcp.NaoEncontradoOuNaoAutorizado ? null : detalhe;
    public static ErroDoBrainMcp NaoEncontrado() => new(CategoriaDeErroMcp.NaoEncontradoOuNaoAutorizado);
    public static ErroDoBrainMcp Revisao() => new(CategoriaDeErroMcp.RevisaoDesatualizada);
    public static ErroDoBrainMcp Entrada(string? detalhe = null) => new(CategoriaDeErroMcp.EntradaInvalida, detalhe);
    public static ErroDoBrainMcp EscopoMudou() => new(CategoriaDeErroMcp.EscopoMudou);
}

public static class ErrosDoBrainMcp
{
    // Detalhes fixos e úteis ao agente; nenhum depende de dado persistido.
    public const string DetalheOrigemLiteral = "Conteúdo não consta da mensagem de origem do usuário; classifique como ConclusaoDoAgente.";
    public const string DetalheCredencial = "Possível credencial detectada; use uma referência secret://host/NOME, nunca o valor.";
    public const string DetalheConteudoOperacional = "Registre somente resultados operacionais selecionados, sem transcript nem raciocínio privado.";
    public const string DetalheSensibilidade = "Agentes registram somente sensibilidade Publico, Pessoal ou Trabalho.";
    public const string DetalheReducaoDeSensibilidade = "A atualização não reduz a sensibilidade do conhecimento.";
    public const string DetalheNatureza = "A correção não altera a natureza do candidato.";
    public const string DetalheIdDeCandidato = "O ID é de um candidato; esta ferramenta aceita somente conhecimento ativo. Para candidatos use brain_corrigir_candidato ou brain_mostrar_origem com origem candidato.";
    public const string DetalheIdDeConhecimento = "O ID é de um conhecimento; esta ferramenta aceita somente candidato. Use brain_mostrar_origem com origem conhecimento.";
    public const string DetalheTiposDaRelacao = "Os tipos dos itens não correspondem à relação: ResolvidoPor exige Incidente → Solucao; ProduziuAprendizado exige Solucao → Aprendizado.";
    public const string DetalheTiposDoVinculo = "O vínculo ResolvidoPor exige candidato Incidente em incidente e candidato Solucao em solucao.";
    public const string DetalheDestinoAtivo = "O destino informado é o contexto ativo; use brain_capturar_conhecimento.";
    public const string DetalheEntradaGrande = "Pedido excede o limite de 32 mil caracteres; reduza os argumentos.";
    public const string DetalheEntradaAbortada = "Pedido excede o limite e não terminou dentro do descarte permitido; canal encerrado. Inicie nova sessão.";
    public const string DetalheRespostaGrande = "Resposta excede o limite; reduza a página ou use consulta mais específica.";
    public const string DetalheLimiteDeChamadas = "Limite de chamadas da sessão atingido; inicie nova sessão.";

    public static string Codigo(CategoriaDeErroMcp categoria) => categoria switch
    {
        CategoriaDeErroMcp.EntradaInvalida => "entrada_invalida",
        CategoriaDeErroMcp.RevisaoDesatualizada => "revisao_desatualizada",
        CategoriaDeErroMcp.NaoEncontradoOuNaoAutorizado => "nao_encontrado_ou_nao_autorizado",
        CategoriaDeErroMcp.EscopoMudou => "escopo_mudou",
        _ => "indisponivel"
    };
    public static string Mensagem(CategoriaDeErroMcp categoria) => categoria switch
    {
        CategoriaDeErroMcp.EntradaInvalida => "Entrada inválida para a ferramenta. Revise os argumentos conforme o schema; repetir a mesma chamada não muda o resultado.",
        CategoriaDeErroMcp.RevisaoDesatualizada => "O item mudou ou seu estado atual não permite a operação. Refaça a consulta e use a revisão atual.",
        CategoriaDeErroMcp.NaoEncontradoOuNaoAutorizado => "Item não encontrado ou não autorizado neste escopo.",
        CategoriaDeErroMcp.EscopoMudou => "Escopo ou conversa mudou; esta sessão foi encerrada. Inicie nova sessão.",
        _ => "Brain indisponível no momento. Tente novamente mais tarde; operações com resultado incerto não devem ser repetidas automaticamente."
    };

    // Mapeamento por tipo. Falha de infraestrutura vence o tipo externo (EF embrulha Npgsql em InvalidOperationException).
    // UnauthorizedAccessException é sempre a categoria unificada: inexistente, outro escopo, sensibilidade ou conteúdo protegido.
    public static CategoriaDeErroMcp Classificar(Exception excecao) => excecao switch
    {
        ErroDoBrainMcp classificado => classificado.Categoria,
        ConflitoDeConcorrenciaException => CategoriaDeErroMcp.RevisaoDesatualizada,
        _ when Infraestrutura(excecao) => CategoriaDeErroMcp.Indisponivel,
        UnauthorizedAccessException => CategoriaDeErroMcp.NaoEncontradoOuNaoAutorizado,
        ArgumentException or JsonException or KeyNotFoundException or FormatException => CategoriaDeErroMcp.EntradaInvalida,
        // Acessor de JsonElement com tipo errado (ex.: GetString em número) é entrada do cliente.
        InvalidOperationException when LancadaPor(excecao, typeof(JsonElement).Assembly) => CategoriaDeErroMcp.EntradaInvalida,
        // Domain/Application usam InvalidOperationException para revisão divergente e estado que não permite a operação.
        // Só essas origens viram revisão: EF/Npgsql/LINQ/objeto descartado são bug ou infraestrutura e não devem induzir retry.
        ObjectDisposedException => CategoriaDeErroMcp.Indisponivel,
        InvalidOperationException when LancadaPor(excecao, typeof(Dante.Domain.Comum.EntidadeBase).Assembly, typeof(ConflitoDeConcorrenciaException).Assembly)
            => CategoriaDeErroMcp.RevisaoDesatualizada,
        _ => CategoriaDeErroMcp.Indisponivel
    };
    // Assembly do método que lançou a exceção (o throw site), sem depender de mensagem.
    private static bool LancadaPor(Exception excecao, params System.Reflection.Assembly[] origens) =>
        excecao.TargetSite?.DeclaringType?.Assembly is { } assembly && origens.Contains(assembly);
    private static bool Infraestrutura(Exception excecao)
    {
        for (Exception? atual = excecao; atual is not null; atual = atual.InnerException)
            if (atual is DbException or TimeoutException or IOException or SocketException or OperationCanceledException) return true;
        return false;
    }

    // Corpo do erro: só código, mensagem fixa e, quando houver, detalhe fixo. Nada da exceção original é serializado.
    public static JsonObject Resultado(Exception excecao) => Resultado(Classificar(excecao), (excecao as ErroDoBrainMcp)?.Detalhe);
    public static JsonObject Resultado(CategoriaDeErroMcp categoria, string? detalhe = null) => ServidorMcpDoBrain.Resultado(detalhe is null
        ? new { erro = Codigo(categoria), mensagem = Mensagem(categoria) }
        : new { erro = Codigo(categoria), mensagem = Mensagem(categoria), detalhe }, true);

    // Resposta ao usuário no Telegram quando a confirmação de uma proposta MCP falha.
    public static string MensagemDeConfirmacao(Exception excecao) => Classificar(excecao) switch
    {
        CategoriaDeErroMcp.EntradaInvalida => "A operação MCP não foi confirmada: ela não é válida para os itens escolhidos (por exemplo, tipos incompatíveis com a relação). Nada foi alterado.",
        CategoriaDeErroMcp.RevisaoDesatualizada => "A operação MCP não foi confirmada: o item mudou ou seu estado atual não permite a operação. Refaça a consulta.",
        CategoriaDeErroMcp.NaoEncontradoOuNaoAutorizado => "A operação MCP não foi confirmada: item não encontrado ou não autorizado neste escopo.",
        CategoriaDeErroMcp.EscopoMudou => "A operação MCP não foi confirmada: o escopo ou a conversa mudou. Prepare a operação novamente.",
        _ => "A operação MCP não foi confirmada: o Brain está indisponível. Tente novamente mais tarde."
    };
}
