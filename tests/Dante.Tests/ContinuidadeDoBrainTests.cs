using System.Collections.Concurrent;
using System.Threading.Channels;
using Dante.Application;
using Dante.Application.Agentes;
using Dante.Application.Anexos;
using Dante.Application.ConstrucaoDeContexto;
using Dante.Application.ConversaDoBrain;
using Dante.Application.MetricasDoBrain;
using Dante.Domain.Conhecimentos;
using Dante.Domain.EspacosDeConhecimento;
using Dante.Infrastructure;
using Dante.Infrastructure.Contextos;
using Dante.Worker.Attachments;
using Dante.Worker.Jobs;
using Dante.Worker.Sessions;
using Dante.Worker.Telegram;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
namespace Dante.Tests;

// #145: o PacoteDeContexto do Brain acompanha a conversa natural (bootstrap/refresh por conversa upstream), o
// ContextoDeTrabalho é atualizado por conversa, e /clear e /compact só afetam a conversa upstream.
public sealed class ContinuidadeDoBrainTests : IAsyncDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"dante-continuidade-"+Guid.NewGuid().ToString("N"));
    private readonly FakeSessionDriverFactory drivers=new();
    private readonly BotApi api=new();
    private string Metricas=>Path.Combine(root,"metricas.jsonl");
    private SessionRegistry? sessions;
    private TelegramPollingService? service;

    [Theory]
    [InlineData("atualize o contexto de trabalho: objetivo: migrar",IntencaoDoBrain.AtualizarContexto,"objetivo: migrar")]
    [InlineData("registre o contexto de trabalho: tarefa: revisar",IntencaoDoBrain.AtualizarContexto,"tarefa: revisar")]
    [InlineData("salve o contexto de trabalho",IntencaoDoBrain.AtualizarContexto,"")]
    [InlineData("mostre o contexto de trabalho",IntencaoDoBrain.MostrarContexto,"")]
    [InlineData("qual é o contexto de trabalho?",IntencaoDoBrain.MostrarContexto,"")]
    [InlineData("registre no Brain: contexto de trabalho importa",IntencaoDoBrain.Capturar,"contexto de trabalho importa")]
    public void ContextoDeTrabalhoTemIntencoesProprias(string texto,IntencaoDoBrain esperado,string corpo)
    {
        var intencao=ResolvedorDeIntencaoDoBrain.Resolver(texto);Assert.Equal(esperado,intencao.Intencao);Assert.Equal(corpo,intencao.Texto);
    }

    [Fact]
    public async Task PacoteAcompanhaOTextoDoTurnoEClearECompactSoReiniciamOEnvio()
    {
        var brain=new ContinuidadeFalsa();await IniciarAsync(continuidade:brain);
        api.Enqueue(Texto("como seguimos?"));
        await Eventually(()=>drivers.Created.Count==1&&drivers.Created[0].TurnInputs.Count==1);
        var driver=drivers.Created[0];
        Assert.Equal("[pacote S000001]\n\nMensagem do usuário:\ncomo seguimos?",driver.TurnInputs[0].Text);
        Assert.Equal(["S000001"],brain.Registrados);
        await ConcluirTurnoAsync(driver);

        api.Enqueue(Texto("/compact"));
        Assert.StartsWith("Compactando",await api.NextMessageAsync());
        Assert.EndsWith("O Brain e o contexto de trabalho não mudam; a próxima mensagem leva de novo o contexto relevante do Brain.",await api.NextMessageAsync());
        Assert.Equal(["S000001"],brain.Reiniciados);
        api.Enqueue(Texto("/clear"));
        Assert.EndsWith("O Brain e o contexto de trabalho não mudam; a próxima mensagem leva de novo o contexto relevante do Brain.",await api.NextMessageAsync());
        Assert.Equal(["S000001","S000001"],brain.Reiniciados);

        api.Enqueue(Texto("/status"));
        Assert.Contains("Brain falso para S000001",await api.NextMessageAsync());
    }

    [Fact]
    public async Task SemBrainOuSemPacoteOTextoSegueIntacto()
    {
        await IniciarAsync(continuidade:new ContinuidadeFalsa{SemPacote=true});
        api.Enqueue(Texto("pergunta comum"));
        await Eventually(()=>drivers.Created.Count==1&&drivers.Created[0].TurnInputs.Count==1);
        Assert.Equal("pergunta comum",drivers.Created[0].TurnInputs[0].Text);
    }

    [PostgreSqlFact]
    public async Task SessaoNovaEOutroAgenteRetomamComBrainESnapshotSemTranscript()
    {
        await using var banco=await Banco.CriarAsync();using var provider=Provider(banco.ConnectionString,out var config);
        await IniciarAsync(new TelegramBrain(provider.GetRequiredService<IServiceScopeFactory>(),config));
        Assert.Contains("Trabalho criado",await Dizer("criar espaço Trabalho"));
        Assert.Contains("candidato",await Dizer("registre no Brain: decisao: entidades usam Guid gerado pelo domínio"));
        Assert.Contains("consolidado",await Dizer("confirmar"));
        Assert.Contains("Contexto de trabalho atualizado",await Dizer("atualize o contexto de trabalho: objetivo: migrar persistência do Brain; tarefa: revisar mapeamentos EF; próximo passo: criar migration de projetos"));
        var mostrado=await Dizer("mostre o contexto de trabalho");Assert.Contains("criar migration de projetos",mostrado);Assert.Contains("Objetivo: migrar persistência do Brain",mostrado);
        Assert.Contains("nada foi alterado",await Dizer("atualize o contexto de trabalho: humor: bom"));

        // Sessão A: bootstrap com conhecimento confirmado e snapshot; o pedido do usuário vem por último.
        api.Enqueue(Texto("qual identidade as entidades usam?"));
        await Eventually(()=>drivers.Created.Count==1&&drivers.Created[0].TurnInputs.Count==1);
        var a=drivers.Created[0];var primeiro=a.TurnInputs[0].Text;
        Assert.StartsWith("Contexto recuperado do Brain",primeiro);Assert.Contains("Guid gerado pelo domínio",primeiro);Assert.Contains("criar migration de projetos",primeiro);
        Assert.EndsWith("[fim do contexto do Brain]\n\nMensagem do usuário:\nqual identidade as entidades usam?",primeiro);
        await ConcluirTurnoAsync(a);

        // Refresh: nada novo nem revisado, então o turno leva só a mensagem.
        api.Enqueue(Texto("e as entidades novas?"));
        await Eventually(()=>a.TurnInputs.Count==2);Assert.Equal("e as entidades novas?",a.TurnInputs[1].Text);
        await ConcluirTurnoAsync(a);
        // Snapshot revisado volta a ser enviado, sozinho.
        Assert.Contains("revisão 2",await Dizer("atualize o contexto de trabalho: progresso: mapeamentos revisados"));
        api.Enqueue(Texto("seguimos"));
        await Eventually(()=>a.TurnInputs.Count==3);var refresh=a.TurnInputs[2].Text;
        Assert.Contains("mapeamentos revisados",refresh);Assert.DoesNotContain("Guid gerado",refresh);
        await ConcluirTurnoAsync(a);

        var status=await Dizer("/status");Assert.Contains("Brain: espaço Trabalho, sem projeto.",status);Assert.Contains("Sessão S000001: 2 item(ns) do Brain já na conversa",status);

        // /clear limpa só a conversa upstream: o próximo turno recebe de novo o contexto (bootstrap).
        Assert.Contains("O Brain e o contexto de trabalho não mudam",await Dizer("/clear"));
        api.Enqueue(Texto("e as entidades?"));
        await Eventually(()=>a.TurnInputs.Count==4);Assert.Contains("Guid gerado pelo domínio",a.TurnInputs[3].Text);
        await ConcluirTurnoAsync(a);

        // Sessão B com o outro agente, sem transcript: conhecimento e snapshot chegam pelo Brain.
        Assert.Contains("encerrada",(await Dizer("/session close")).ToLowerInvariant());
        Assert.Contains("Codex",await Dizer("/agent set codex"));
        api.Enqueue(Texto("retomando: qual identidade usar nas entidades?"));
        await Eventually(()=>drivers.Created.Count==2&&drivers.Created[1].TurnInputs.Count==1);
        var b=drivers.Created[1].TurnInputs[0].Text;
        Assert.Contains("Guid gerado pelo domínio",b);Assert.Contains("criar migration de projetos",b);Assert.Contains("mapeamentos revisados",b);
        Assert.DoesNotContain("qual identidade as entidades usam",b);

        await using var c=banco.Contexto();
        var k=await c.Conhecimentos.SingleAsync();Assert.Equal(StatusDoConhecimento.Confirmado,k.Status);Assert.Equal(TipoDeConhecimento.Decisao,k.Tipo);
        Assert.Equal(2,(await c.Set<Dante.Domain.ContextosDeTrabalho.ContextoDeTrabalho>().SingleAsync()).Revisao);
    }

    [PostgreSqlFact]
    public async Task TrocaDeEscopoComSessaoAtivaAvisaEEnviaOContextoDoNovoEscopo()
    {
        await using var banco=await Banco.CriarAsync();using var provider=Provider(banco.ConnectionString,out var config);
        await IniciarAsync(new TelegramBrain(provider.GetRequiredService<IServiceScopeFactory>(),config));
        await Dizer("criar espaço Pessoal");await Dizer("registre no Brain: deadlock resolvido com ordem consistente de locks");await Dizer("confirmar");
        api.Enqueue(Texto("como evitar deadlock?"));
        await Eventually(()=>drivers.Created.Count==1&&drivers.Created[0].TurnInputs.Count==1);
        var a=drivers.Created[0];Assert.Contains("ordem consistente",a.TurnInputs[0].Text);
        await ConcluirTurnoAsync(a);

        var aviso=await Dizer("criar projeto Dante");
        Assert.Contains("A sessão ativa S000001 (Claude, General) continua",aviso);Assert.Contains("/clear",aviso);
        Assert.Contains("o escopo mudou",await Dizer("/status"));
        await Dizer("atualize o contexto de trabalho: objetivo: lançar o Dante; tarefa: revisar deadlock no projeto");
        api.Enqueue(Texto("e o deadlock?"));
        await Eventually(()=>a.TurnInputs.Count==2);var texto=a.TurnInputs[1].Text;
        Assert.Contains("O escopo do Brain mudou",texto);Assert.Contains("lançar o Dante",texto);Assert.DoesNotContain("ordem consistente",texto);
    }

    [PostgreSqlFact]
    public async Task RespostaDoAgenteCitadaViraCandidatoEPacoteRespeitaOrcamentoESensibilidade()
    {
        await using var banco=await Banco.CriarAsync();using var provider=Provider(banco.ConnectionString,out var config);
        var brain=new TelegramBrain(provider.GetRequiredService<IServiceScopeFactory>(),config);await IniciarAsync(brain);
        await Dizer("criar espaço Pessoal");
        var resposta=new TelegramMessage(new(123),"Use ordem consistente de locks para evitar deadlock.",null,MessageId:900);
        api.Enqueue(new TelegramMessage(new(123),"documente isso",new(123),MessageId:901,ReplyToMessage:resposta));
        Assert.Contains("candidato",await api.NextMessageAsync());
        await using(var c=banco.Contexto())
        {
            Assert.Empty(await c.Conhecimentos.ToListAsync());
            Assert.Equal("Use ordem consistente de locks para evitar deadlock.",(await c.CandidatosDeConhecimento.SingleAsync()).Conteudo);
        }
        api.Enqueue(new TelegramMessage(new(123),"texto comum",new(123),MessageId:902,ReplyToMessage:resposta));
        Assert.Contains("não está disponível",await api.NextMessageAsync());

        EspacoDeConhecimento e;await using(var c=banco.Contexto())e=await c.EspacosDeConhecimento.SingleAsync();
        await using(var c=banco.Contexto())
        {
            c.AddRange(Enumerable.Range(1,30).Select(i=>Novo(e,$"deadlock caso {i}: "+new string('x',600))));
            c.Add(Novo(e,"deadlock segredo reservado",Sensibilidade.Secreto));await c.SaveChangesAsync();
        }
        var preparado=await brain.PrepararAsync(Texto("como evitar deadlock?"),"S000099","como evitar deadlock?");
        Assert.NotNull(preparado);Assert.True(preparado.Bootstrap);
        Assert.True(preparado.Pacote.Custo.TokensEstimados<=2048);Assert.InRange(preparado.Pacote.Itens.Count,1,12);
        Assert.Contains(preparado.Pacote.Registros,x=>x.Motivo.Contains("orçamento",StringComparison.Ordinal)||x.Motivo=="Limite de itens.");
        Assert.DoesNotContain("segredo reservado",preparado.Texto);
        await brain.RegistrarInjecaoAsync(preparado);
        var refresh=await brain.PrepararAsync(Texto("como evitar deadlock?"),"S000099","como evitar deadlock?");
        Assert.NotNull(refresh);Assert.False(refresh.Bootstrap);Assert.True(refresh.Pacote.Custo.TokensEstimados<=1024);
        Assert.Empty(refresh.Pacote.Itens.Select(x=>x.Chave).Intersect(preparado.Pacote.Itens.Select(x=>x.Chave)));
        Assert.Contains(refresh.Pacote.Registros,x=>x.Motivo.StartsWith("Já injetado",StringComparison.Ordinal));
        brain.ReiniciarSessao("S000099");
        Assert.True((await brain.PrepararAsync(Texto("como evitar deadlock?"),"S000099","como evitar deadlock?"))!.Bootstrap);
    }

    // #148: a sessão A mede o histórico bruto; a sessão B, retomada pelo Brain, é comparada com ele e avaliada.
    [PostgreSqlFact]
    public async Task RetomadaGeraMetricasComparaveisSemConteudo()
    {
        await using var banco=await Banco.CriarAsync();using var provider=Provider(banco.ConnectionString,out var config);
        await IniciarAsync(new TelegramBrain(provider.GetRequiredService<IServiceScopeFactory>(),config));
        await Dizer("criar espaço Infra");
        Assert.Contains("Nenhuma conversa",await Dizer("avalie a retomada: repetições: 0"));
        api.Enqueue(Texto("como configuro o banco local?"));
        await Eventually(()=>drivers.Created.Count==1&&drivers.Created[0].TurnInputs.Count==1);
        Assert.Equal("como configuro o banco local?",drivers.Created[0].TurnInputs[0].Text);
        await ConcluirTurnoAsync(drivers.Created[0],string.Join(' ',Enumerable.Repeat("detalhe",300)));
        await Dizer("registre no Brain: decisao: banco PostgreSQL local na porta 5433");await Dizer("confirmar");
        await Dizer("atualize o contexto de trabalho: objetivo: configurar banco; próximo passo: rodar migrations");
        Assert.Contains("dados insuficientes",await Dizer("métricas do Brain"));

        Assert.Contains("encerrada",await Dizer("/session close"));
        api.Enqueue(Texto("retomando o banco local"));
        await Eventually(()=>drivers.Created.Count==2&&drivers.Created[1].TurnInputs.Count==1);
        Assert.Contains("porta 5433",drivers.Created[1].TurnInputs[0].Text);
        await ConcluirTurnoAsync(drivers.Created[1]);

        var resumo=await Dizer("métricas do Brain");
        Assert.Contains("Sessões medidas: 2; envios: 2 (2 iniciais); turnos: 2.",resumo);Assert.Contains("Retomadas: 1.",resumo);
        Assert.Contains("- S000002 (Claude): Brain ",resumo);Assert.Contains("Indicação: ganho",resumo);
        Assert.Contains("entrada indisponível, saída indisponível (2 turno(s) sem dado)",resumo);Assert.Contains("1 retomada(s) sem avaliação",resumo);
        Assert.Contains("nada foi registrado",await Dizer("avalie a retomada: humor: bom"));
        Assert.Contains("sessão S000002",await Dizer("avalie a retomada: repetições: 0; esclarecimentos: 0; concluída: sim; contexto adicional: não; relevantes: 2; irrelevantes: 0"));
        resumo=await Dizer("métricas do Brain");
        Assert.Contains("repetições 0, esclarecimentos 0, concluída sim, contexto adicional não, incorretos ?, relevantes 2/2",resumo);
        Assert.Contains("Qualidade: nenhum problema relatado; precisão avaliada 1,00.",resumo);

        var arquivo=await File.ReadAllTextAsync(Metricas);
        Assert.Contains("S000002",arquivo);
        foreach(var conteudo in new[]{"banco local","PostgreSQL","detalhe","rodar migrations","Infra"})Assert.DoesNotContain(conteudo,arquivo);
    }

    // Review do #194: turno A ativo no espaço → troca para um projeto → mensagem B enfileirada → A e B terminam. Cada
    // Turno fica no escopo do envio que abriu o turno, não no escopo selecionado quando ele termina.
    [PostgreSqlFact]
    public async Task TurnoEnfileiradoDepoisDaTrocaDeEscopoMedeCadaTurnoNoSeuEscopo()
    {
        await using var banco=await Banco.CriarAsync();using var provider=Provider(banco.ConnectionString,out var config);
        await IniciarAsync(new TelegramBrain(provider.GetRequiredService<IServiceScopeFactory>(),config));
        await Dizer("criar espaço Pessoal");
        api.Enqueue(Texto("mensagem do escopo A"));
        await Eventually(()=>drivers.Created.Count==1&&drivers.Created[0].TurnInputs.Count==1);
        var driver=drivers.Created[0];
        Assert.Contains("criado e selecionado",await Dizer("criar projeto Dante"));
        Assert.StartsWith("Recebido",await Dizer("mensagem do escopo B"));

        driver.Emit(new TurnStartedEvent());driver.Emit(new MessageCompletedEvent("m1","resposta A"));driver.Emit(new TurnCompletedEvent(AgentTurnOutcome.Completed));
        Assert.Equal("resposta A\n",await api.NextMessageAsync());
        await Eventually(()=>driver.TurnInputs.Count==2);Assert.Equal("mensagem do escopo B",driver.TurnInputs[1].Text);
        driver.Emit(new TurnStartedEvent());driver.Emit(new MessageCompletedEvent("m2","resposta B mais longa"));driver.Emit(new TurnCompletedEvent(AgentTurnOutcome.Completed));
        Assert.Equal("resposta B mais longa\n",await api.NextMessageAsync());

        Guid projeto;await using(var c=banco.Contexto())projeto=(await c.Projetos.SingleAsync()).Id;
        MetricaDoBrainDto[] metricas=[];
        await Eventually(()=>(metricas=File.ReadAllLines(Metricas).Select(x=>System.Text.Json.JsonSerializer.Deserialize<MetricaDoBrainDto>(x)!).ToArray())
            .Count(x=>x.Tipo==MetricaDoBrainDto.Turno)==2);
        var envios=metricas.Where(x=>x.Tipo==MetricaDoBrainDto.Envio).ToArray();var turnos=metricas.Where(x=>x.Tipo==MetricaDoBrainDto.Turno).ToArray();
        Assert.Equal([null,projeto],envios.Select(x=>x.IdProjeto));
        Assert.Null(turnos[0].IdProjeto);Assert.Equal(ConstrutorDeContextoAppService.EstimarTokens("resposta A"),turnos[0].TokensDaResposta);
        Assert.Equal(projeto,turnos[1].IdProjeto);Assert.Equal(ConstrutorDeContextoAppService.EstimarTokens("resposta B mais longa"),turnos[1].TokensDaResposta);
        Assert.All(metricas,x=>Assert.Equal(envios[0].IdSessao,x.IdSessao));
    }

    private static Conhecimento Novo(EspacoDeConhecimento e,string texto,Sensibilidade classe=Sensibilidade.Pessoal)
    {var k=new Conhecimento(e.Id,null,TipoDeConhecimento.Fato,texto,null,StatusDoConhecimento.Inferido,null,classe,null,null,[],new(e.IdUsuario,"manual","fonte:teste"),DateTimeOffset.UtcNow);k.Confirmar(1,k.Proveniencia,DateTimeOffset.UtcNow);return k;}

    private ServiceProvider Provider(string connection,out IConfiguration config)
    {
        config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["ConnectionStrings:Dante"]=connection,["Telegram:AllowedUserIds"]="123",["DANTE_BRAIN_METRICS_FILE"]=Metricas}).Build();
        return new ServiceCollection().AddApplication().AddInfrastructure(config).BuildServiceProvider();
    }

    private async Task<string> Dizer(string texto)
    {
        api.Enqueue(Texto(texto));return await api.NextMessageAsync();
    }

    private async Task ConcluirTurnoAsync(FakeSessionDriver driver,string resposta="resposta")
    {
        driver.Emit(new TurnStartedEvent());driver.Emit(new MessageCompletedEvent("m1",resposta));driver.Emit(new TurnCompletedEvent(AgentTurnOutcome.Completed));
        Assert.Equal(resposta+"\n",await api.NextMessageAsync());
        await Eventually(()=>sessions!.GetActive(123)!.State==AgentSessionState.Idle);
    }

    private async Task IniciarAsync(TelegramBrain? brain=null,IContinuidadeDoBrain? continuidade=null)
    {
        var options=Options.Create(new TelegramOptions{BotToken="test",AllowedUserIds="123"});
        var store=new AttachmentStore(Path.Combine(root,"attachments"));
        var delivery=new TelegramDeliveryService(api,NullLogger<TelegramDeliveryService>.Instance);
        sessions=new SessionRegistry(drivers,NullLogger<SessionRegistry>.Instance,brain is null?delivery:new MetricasDeSessaoDoBrain(delivery,brain),attachments:store);
        service=new TelegramPollingService(api,options,new TelegramUserAuthorizer(options),Runner.Instance,Runner.Instance,new JobRegistry(),
            NullLogger<TelegramPollingService>.Instance,null,new GeneralWorkspace(Path.Combine(root,"general")),
            new AssistantSettingsStore(Path.Combine(root,"settings.json")),sessions,delivery,attachments:store,
            pendingAttachments:new PendingAttachments(store),brain:brain,continuidade:continuidade);
        await service.StartAsync(CancellationToken.None);
    }

    private static TelegramMessage Texto(string texto)=>new(new TelegramChat(123),texto,new TelegramUser(123));

    private static async Task Eventually(Func<bool> condition)
    {
        for(var i=0;i<200&&!condition();i++)await Task.Delay(50);
        Assert.True(condition());
    }

    public async ValueTask DisposeAsync()
    {
        if(service is not null){await service.StopAsync(CancellationToken.None);service.Dispose();}
        if(sessions is not null)await sessions.DisposeAsync();
        if(Directory.Exists(root))Directory.Delete(root,true);
    }

    private sealed class ContinuidadeFalsa:IContinuidadeDoBrain
    {
        public bool SemPacote{get;init;}
        public List<string> Registrados{get;}=[];
        public List<string> Reiniciados{get;}=[];
        public bool Configurado=>true;
        public Task<ContextoParaTurno?> PrepararAsync(TelegramMessage mensagem,string idSessao,string texto,CancellationToken cancellationToken=default)=>
            Task.FromResult(SemPacote?null:new ContextoParaTurno(idSessao,"escopo",$"[pacote {idSessao}]\n\nMensagem do usuário:\n{texto}",
                new PacoteDeContextoDto(Guid.NewGuid(),"",[],[],new(0,0,0,0,2048),false),true,new()));
        public Task RegistrarInjecaoAsync(ContextoParaTurno contexto,CancellationToken cancellationToken=default){Registrados.Add(contexto.IdSessao);return Task.CompletedTask;}
        public Task RegistrarTurnoAsync(string? correlacao,string agente,int tokensDaResposta,AgentTurnOutcome resultado,AgentTokenUsage? uso,CancellationToken cancellationToken=default)=>Task.CompletedTask;
        public void ReiniciarSessao(string idSessao)=>Reiniciados.Add(idSessao);
        public string? EscopoSelecionado(TelegramMessage mensagem)=>"escopo";
        public string? DescreverStatus(TelegramMessage mensagem,string? idSessao)=>$"Brain falso para {idSessao}";
    }

    private sealed class Runner:ICodexRunner,IClaudeRunner
    {
        public static Runner Instance{get;}=new();
        public Task<AgentProcessResult> RunAsync(string prompt,string workingDirectory,CancellationToken cancellationToken=default,bool generalMode=false,
            IReadOnlyDictionary<string,string>? environment=null,string? model=null,string? effort=null,IReadOnlyList<Attachment>? attachments=null)=>
            throw new InvalidOperationException("no one-shot expected");
    }

    private sealed class BotApi:ITelegramBotApi
    {
        private readonly Channel<TelegramUpdate> updates=Channel.CreateUnbounded<TelegramUpdate>();
        private readonly Channel<string> messages=Channel.CreateUnbounded<string>();
        private long nextId;
        public void Enqueue(TelegramMessage message)=>updates.Writer.TryWrite(new TelegramUpdate(Interlocked.Increment(ref nextId),message));
        public async Task<string> NextMessageAsync()=>await messages.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset,CancellationToken cancellationToken)=>[await updates.Reader.ReadAsync(cancellationToken)];
        public Task SendMessageAsync(long chatId,string text,CancellationToken cancellationToken){messages.Writer.TryWrite(text);return Task.CompletedTask;}
    }
}
