using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Dante.Infrastructure.Data.Orquestracao.Migrations
{
    /// <inheritdoc />
    public partial class CoordenacaoPorPapel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_execucoes_roteiro_com_grupo",
                schema: "orquestracao",
                table: "execucoes");

            migrationBuilder.AddColumn<string>(
                name: "papel_do_coordenador",
                schema: "orquestracao",
                table: "versoes_de_grupo",
                type: "character varying(31)",
                maxLength: 31,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "finalidade",
                schema: "orquestracao",
                table: "pedidos_de_aprovacao",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "id_plano",
                schema: "orquestracao",
                table: "pedidos_de_aprovacao",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "id_plano",
                schema: "orquestracao",
                table: "etapas_da_execucao",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "unidade",
                schema: "orquestracao",
                table: "etapas_da_execucao",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "coordenacoes_da_execucao",
                schema: "orquestracao",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    id_execucao = table.Column<Guid>(type: "uuid", nullable: false),
                    id_grupo = table.Column<Guid>(type: "uuid", nullable: false),
                    nome_do_grupo = table.Column<string>(type: "character varying(31)", maxLength: 31, nullable: false),
                    id_versao_do_grupo = table.Column<Guid>(type: "uuid", nullable: false),
                    numero_da_versao_do_grupo = table.Column<int>(type: "integer", nullable: false),
                    hash_da_versao_do_grupo = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    papel_do_coordenador = table.Column<string>(type: "character varying(31)", maxLength: 31, nullable: false),
                    hash_do_snapshot = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    objetivo_redigido = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    objetivo_alterado_pela_redacao = table.Column<bool>(type: "boolean", nullable: false),
                    hash_do_objetivo_original = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_coordenacoes_da_execucao", x => x.id);
                    table.CheckConstraint("ck_coordenacoes_da_execucao_hashes", "hash_da_versao_do_grupo ~ '^[0-9a-f]{64}$' AND hash_do_snapshot ~ '^[0-9a-f]{64}$' AND hash_do_objetivo_original ~ '^[0-9a-f]{64}$'");
                    table.ForeignKey(
                        name: "FK_coordenacoes_da_execucao_execucoes_id_execucao",
                        column: x => x.id_execucao,
                        principalSchema: "orquestracao",
                        principalTable: "execucoes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_coordenacoes_da_execucao_grupos_de_agentes_id_grupo",
                        column: x => x.id_grupo,
                        principalSchema: "orquestracao",
                        principalTable: "grupos_de_agentes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_coordenacoes_da_execucao_versoes_de_grupo_id_versao_do_grupo",
                        column: x => x.id_versao_do_grupo,
                        principalSchema: "orquestracao",
                        principalTable: "versoes_de_grupo",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "planos_da_coordenacao",
                schema: "orquestracao",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    id_execucao = table.Column<Guid>(type: "uuid", nullable: false),
                    revisao = table.Column<int>(type: "integer", nullable: false),
                    id_plano_anterior = table.Column<Guid>(type: "uuid", nullable: true),
                    motivo = table.Column<int>(type: "integer", nullable: false),
                    conteudo = table.Column<string>(type: "text", nullable: false),
                    hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    quantidade_de_unidades = table.Column<int>(type: "integer", nullable: false),
                    estado = table.Column<int>(type: "integer", nullable: false),
                    criado_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decidido_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    substituido_em = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_planos_da_coordenacao", x => x.id);
                    table.CheckConstraint("ck_planos_da_coordenacao_decisao", "(estado = 0) = (decidido_em IS NULL AND substituido_em IS NULL) OR estado = 3");
                    table.CheckConstraint("ck_planos_da_coordenacao_hash", "hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_planos_da_coordenacao_revisao", "revisao BETWEEN 1 AND 3 AND (revisao = 1) = (id_plano_anterior IS NULL) AND (revisao = 1) = (motivo = 0)");
                    table.CheckConstraint("ck_planos_da_coordenacao_unidades", "quantidade_de_unidades BETWEEN 1 AND 4");
                    table.ForeignKey(
                        name: "FK_planos_da_coordenacao_execucoes_id_execucao",
                        column: x => x.id_execucao,
                        principalSchema: "orquestracao",
                        principalTable: "execucoes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_planos_da_coordenacao_planos_da_coordenacao_id_plano_anteri~",
                        column: x => x.id_plano_anterior,
                        principalSchema: "orquestracao",
                        principalTable: "planos_da_coordenacao",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_versoes_de_grupo_coordenador",
                schema: "orquestracao",
                table: "versoes_de_grupo",
                sql: "(modo = 1) = (papel_do_coordenador IS NOT NULL) AND (modo <> 1 OR (cardinality(roteiro) = 0 AND rodadas_de_revisao_maximas = 0))");

            migrationBuilder.CreateIndex(
                name: "IX_pedidos_de_aprovacao_id_plano",
                schema: "orquestracao",
                table: "pedidos_de_aprovacao",
                column: "id_plano",
                unique: true,
                filter: "id_plano IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_pedidos_de_aprovacao_finalidade",
                schema: "orquestracao",
                table: "pedidos_de_aprovacao",
                sql: "(finalidade = 1) = (id_plano IS NOT NULL) AND finalidade IN (0, 1)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_execucoes_roteiro_com_grupo",
                schema: "orquestracao",
                table: "execucoes",
                sql: "(tipo IN (2, 4)) = (id_versao_do_grupo IS NOT NULL) AND (tipo <> 2 OR id_versao_do_agente IS NULL) AND (tipo <> 4 OR id_versao_do_agente IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_etapas_da_execucao_id_plano_unidade",
                schema: "orquestracao",
                table: "etapas_da_execucao",
                columns: new[] { "id_plano", "unidade" },
                unique: true,
                filter: "id_plano IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_etapas_da_execucao_unidade_do_plano",
                schema: "orquestracao",
                table: "etapas_da_execucao",
                sql: "(id_plano IS NULL) = (unidade IS NULL) AND (tipo = 3) = (id_plano IS NOT NULL) AND (id_plano IS NULL OR (NOT escrita AND passo IS NULL AND papel IS NOT NULL))");

            migrationBuilder.CreateIndex(
                name: "IX_coordenacoes_da_execucao_id_execucao",
                schema: "orquestracao",
                table: "coordenacoes_da_execucao",
                column: "id_execucao",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_coordenacoes_da_execucao_id_grupo",
                schema: "orquestracao",
                table: "coordenacoes_da_execucao",
                column: "id_grupo");

            migrationBuilder.CreateIndex(
                name: "IX_coordenacoes_da_execucao_id_versao_do_grupo",
                schema: "orquestracao",
                table: "coordenacoes_da_execucao",
                column: "id_versao_do_grupo");

            migrationBuilder.CreateIndex(
                name: "IX_planos_da_coordenacao_id_execucao_revisao",
                schema: "orquestracao",
                table: "planos_da_coordenacao",
                columns: new[] { "id_execucao", "revisao" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_planos_da_coordenacao_id_plano_anterior",
                schema: "orquestracao",
                table: "planos_da_coordenacao",
                column: "id_plano_anterior",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_etapas_da_execucao_planos_da_coordenacao_id_plano",
                schema: "orquestracao",
                table: "etapas_da_execucao",
                column: "id_plano",
                principalSchema: "orquestracao",
                principalTable: "planos_da_coordenacao",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_pedidos_de_aprovacao_planos_da_coordenacao_id_plano",
                schema: "orquestracao",
                table: "pedidos_de_aprovacao",
                column: "id_plano",
                principalSchema: "orquestracao",
                principalTable: "planos_da_coordenacao",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // Coordenação por papel (AD-75, #310): o banco protege, além do domínio, o snapshot, as revisões do plano, a
            // aprovação vinculada ao hash, a materialização só de plano aprovado com participantes do snapshot, a ordem
            // topológica das dependências e o despacho só pelo plano aprovado.
            migrationBuilder.Sql("""
                -- Grupo coordenado: nenhum participante escreve (escrita depende do gateway #311).
                CREATE FUNCTION orquestracao.conferir_participante_de_grupo_coordenado() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.pode_escrever AND EXISTS (SELECT 1 FROM orquestracao.versoes_de_grupo v
                                                     WHERE v.id = NEW.id_versao_do_grupo AND v.modo = 1) THEN
                        RAISE EXCEPTION 'grupo coordenado não tem participante com escrita' USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER trg_participantes_de_grupo_coordenado BEFORE INSERT ON orquestracao.participantes_de_grupo
                    FOR EACH ROW EXECUTE FUNCTION orquestracao.conferir_participante_de_grupo_coordenado();

                -- Snapshot da coordenação: nasce para uma Coordenada, na versão de grupo dela; depois, só o objetivo
                -- protegido pela redação pode ser trocado por um utilizável; nunca removido.
                CREATE FUNCTION orquestracao.proteger_coordenacao_da_execucao() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'coordenações não são removidas' USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF TG_OP = 'INSERT' THEN
                        IF NOT EXISTS (SELECT 1 FROM orquestracao.execucoes e WHERE e.id = NEW.id_execucao AND e.tipo = 4
                                       AND e.id_versao_do_grupo = NEW.id_versao_do_grupo) THEN
                            RAISE EXCEPTION 'snapshot de coordenação só de uma Coordenada, na versão dela'
                                USING ERRCODE = 'check_violation';
                        END IF;
                        RETURN NEW;
                    END IF;
                    IF (NEW.id_execucao, NEW.id_grupo, NEW.nome_do_grupo, NEW.id_versao_do_grupo, NEW.numero_da_versao_do_grupo,
                        NEW.hash_da_versao_do_grupo, NEW.papel_do_coordenador, NEW.hash_do_snapshot)
                       IS DISTINCT FROM
                       (OLD.id_execucao, OLD.id_grupo, OLD.nome_do_grupo, OLD.id_versao_do_grupo, OLD.numero_da_versao_do_grupo,
                        OLD.hash_da_versao_do_grupo, OLD.papel_do_coordenador, OLD.hash_do_snapshot) THEN
                        RAISE EXCEPTION 'o snapshot da coordenação é imutável' USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF (NEW.objetivo_redigido, NEW.objetivo_alterado_pela_redacao, NEW.hash_do_objetivo_original)
                       IS DISTINCT FROM (OLD.objetivo_redigido, OLD.objetivo_alterado_pela_redacao, OLD.hash_do_objetivo_original)
                       AND (NOT OLD.objetivo_alterado_pela_redacao OR NEW.objetivo_alterado_pela_redacao) THEN
                        RAISE EXCEPTION 'só um objetivo protegido pela redação é substituído, e por um utilizável'
                            USING ERRCODE = 'restrict_violation';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER trg_coordenacoes_da_execucao_snapshot BEFORE INSERT OR UPDATE OR DELETE
                    ON orquestracao.coordenacoes_da_execucao
                    FOR EACH ROW EXECUTE FUNCTION orquestracao.proteger_coordenacao_da_execucao();

                -- Revisão do plano: nasce aguardando decisão, numa Coordenada, com o hash dos bytes do conteúdo e, se
                -- substituta, logo depois da anterior da mesma execução. Depois: conteúdo, hash e identidade imutáveis;
                -- só as transições do domínio; terminal não muda; nunca removida.
                CREATE FUNCTION orquestracao.proteger_plano_da_coordenacao() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'planos não são removidos' USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF TG_OP = 'INSERT' THEN
                        IF NEW.estado <> 0 THEN
                            RAISE EXCEPTION 'plano nasce aguardando aprovação' USING ERRCODE = 'check_violation';
                        END IF;
                        IF NOT EXISTS (SELECT 1 FROM orquestracao.execucoes e WHERE e.id = NEW.id_execucao AND e.tipo = 4) THEN
                            RAISE EXCEPTION 'plano só numa Coordenada' USING ERRCODE = 'check_violation';
                        END IF;
                        IF NEW.hash <> encode(sha256(convert_to(NEW.conteudo, 'UTF8')), 'hex') THEN
                            RAISE EXCEPTION 'o hash do plano não é o do conteúdo' USING ERRCODE = 'check_violation';
                        END IF;
                        IF NEW.id_plano_anterior IS NOT NULL AND NOT EXISTS (
                               SELECT 1 FROM orquestracao.planos_da_coordenacao a WHERE a.id = NEW.id_plano_anterior
                               AND a.id_execucao = NEW.id_execucao AND a.revisao = NEW.revisao - 1) THEN
                            RAISE EXCEPTION 'revisão fora da cadeia do plano' USING ERRCODE = 'check_violation';
                        END IF;
                        RETURN NEW;
                    END IF;
                    IF (NEW.id, NEW.id_execucao, NEW.revisao, NEW.id_plano_anterior, NEW.motivo, NEW.conteudo, NEW.hash,
                        NEW.quantidade_de_unidades, NEW.criado_em)
                       IS DISTINCT FROM
                       (OLD.id, OLD.id_execucao, OLD.revisao, OLD.id_plano_anterior, OLD.motivo, OLD.conteudo, OLD.hash,
                        OLD.quantidade_de_unidades, OLD.criado_em) THEN
                        RAISE EXCEPTION 'o conteúdo e a identidade do plano são imutáveis' USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF OLD.estado IN (2, 3, 4) THEN
                        RAISE EXCEPTION 'plano encerrado é imutável' USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF NEW.estado <> OLD.estado AND NOT (
                           (OLD.estado = 0 AND NEW.estado IN (1, 2, 3, 4)) OR (OLD.estado = 1 AND NEW.estado = 3)) THEN
                        RAISE EXCEPTION 'transição de plano inválida' USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER trg_planos_da_coordenacao_protegidos BEFORE INSERT OR UPDATE OR DELETE
                    ON orquestracao.planos_da_coordenacao
                    FOR EACH ROW EXECUTE FUNCTION orquestracao.proteger_plano_da_coordenacao();

                -- No commit: plano Aprovado ou Recusado só com a decisão correspondente do gate sobre o mesmo hash; uma
                -- revisão substituta só com a anterior Substituido.
                CREATE FUNCTION orquestracao.conferir_decisao_do_plano() RETURNS trigger
                LANGUAGE plpgsql AS $$
                DECLARE
                    atual orquestracao.planos_da_coordenacao%ROWTYPE;
                BEGIN
                    SELECT * INTO atual FROM orquestracao.planos_da_coordenacao WHERE id = NEW.id;
                    IF atual.estado IN (1, 2) AND NOT EXISTS (
                           SELECT 1 FROM orquestracao.pedidos_de_aprovacao p WHERE p.id_plano = atual.id AND p.finalidade = 1
                           AND p.hash_do_artefato = atual.hash AND p.estado = CASE atual.estado WHEN 1 THEN 1 ELSE 3 END) THEN
                        RAISE EXCEPTION 'decisão do plano sem a decisão do gate sobre o mesmo hash'
                            USING ERRCODE = 'check_violation';
                    END IF;
                    IF atual.id_plano_anterior IS NOT NULL AND NOT EXISTS (
                           SELECT 1 FROM orquestracao.planos_da_coordenacao a WHERE a.id = atual.id_plano_anterior AND a.estado = 3) THEN
                        RAISE EXCEPTION 'revisão substituta sem a anterior substituída' USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NULL;
                END $$;
                CREATE CONSTRAINT TRIGGER trg_planos_da_coordenacao_decisao AFTER INSERT OR UPDATE
                    ON orquestracao.planos_da_coordenacao DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION orquestracao.conferir_decisao_do_plano();

                -- Pedido de aprovação: a finalidade decide o que ele pode decidir. Artefato do roteiro só num Roteiro;
                -- plano só numa Coordenada, sobre a revisão que aguarda decisão, com o hash dela e o do snapshot.
                CREATE FUNCTION orquestracao.conferir_pedido_de_aprovacao() RETURNS trigger
                LANGUAGE plpgsql AS $$
                DECLARE
                    plano orquestracao.planos_da_coordenacao%ROWTYPE;
                BEGIN
                    IF NEW.finalidade = 0 THEN
                        IF NOT EXISTS (SELECT 1 FROM orquestracao.execucoes e WHERE e.id = NEW.id_execucao AND e.tipo = 2) THEN
                            RAISE EXCEPTION 'aprovação de artefato só num Roteiro' USING ERRCODE = 'check_violation';
                        END IF;
                        RETURN NEW;
                    END IF;
                    SELECT * INTO plano FROM orquestracao.planos_da_coordenacao WHERE id = NEW.id_plano;
                    IF plano.id IS NULL OR plano.id_execucao <> NEW.id_execucao OR plano.estado <> 0
                       OR plano.hash <> NEW.hash_do_artefato OR plano.revisao <> NEW.rodada
                       OR NOT EXISTS (SELECT 1 FROM orquestracao.coordenacoes_da_execucao c WHERE c.id_execucao = NEW.id_execucao
                                      AND c.hash_do_snapshot = NEW.hash_do_snapshot) THEN
                        RAISE EXCEPTION 'aprovação de plano só do plano que aguarda decisão, com o hash dele'
                            USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER trg_pedidos_de_aprovacao_conferidos BEFORE INSERT ON orquestracao.pedidos_de_aprovacao
                    FOR EACH ROW EXECUTE FUNCTION orquestracao.conferir_pedido_de_aprovacao();
                CREATE OR REPLACE FUNCTION orquestracao.proteger_pedido_de_aprovacao() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'pedidos de aprovação não são removidos' USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF OLD.estado <> 0 THEN
                        RAISE EXCEPTION 'pedido de aprovação já decidido' USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF (NEW.id_execucao, NEW.id_etapa, NEW.rodada, NEW.hash_do_snapshot, NEW.hash_do_artefato, NEW.criado_em,
                        NEW.finalidade, NEW.id_plano)
                       IS DISTINCT FROM (OLD.id_execucao, OLD.id_etapa, OLD.rodada, OLD.hash_do_snapshot, OLD.hash_do_artefato,
                        OLD.criado_em, OLD.finalidade, OLD.id_plano) THEN
                        RAISE EXCEPTION 'o artefato de um pedido de aprovação é imutável' USING ERRCODE = 'restrict_violation';
                    END IF;
                    RETURN NEW;
                END $$;

                -- Etapa de unidade: só de um plano aprovado da mesma execução, para um participante do snapshot que não
                -- é o coordenador (conferido no commit, depois da aprovação gravada na mesma transação); o vínculo com o
                -- plano, a unidade, o papel e a versão nunca mudam.
                CREATE FUNCTION orquestracao.proteger_unidade_do_plano() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF (NEW.id_plano, NEW.unidade) IS DISTINCT FROM (OLD.id_plano, OLD.unidade)
                       OR (OLD.id_plano IS NOT NULL AND (NEW.papel, NEW.id_versao_do_agente) IS DISTINCT FROM (OLD.papel, OLD.id_versao_do_agente)) THEN
                        RAISE EXCEPTION 'a unidade de plano de uma etapa é imutável' USING ERRCODE = 'restrict_violation';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER trg_etapas_da_execucao_unidade_imutavel BEFORE UPDATE ON orquestracao.etapas_da_execucao
                    FOR EACH ROW EXECUTE FUNCTION orquestracao.proteger_unidade_do_plano();

                CREATE FUNCTION orquestracao.conferir_unidade_do_plano() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM orquestracao.planos_da_coordenacao p WHERE p.id = NEW.id_plano
                                   AND p.id_execucao = NEW.id_execucao AND p.estado = 1)
                       OR NOT EXISTS (SELECT 1 FROM orquestracao.participantes_da_execucao pe
                                      JOIN orquestracao.coordenacoes_da_execucao c ON c.id_execucao = pe.id_execucao
                                      WHERE pe.id_execucao = NEW.id_execucao AND pe.papel = NEW.papel
                                        AND pe.id_versao_do_agente = NEW.id_versao_do_agente AND pe.papel <> c.papel_do_coordenador) THEN
                        RAISE EXCEPTION 'unidade fora de plano aprovado ou do snapshot da execução' USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NULL;
                END $$;
                CREATE CONSTRAINT TRIGGER trg_etapas_da_execucao_unidade_do_plano AFTER INSERT ON orquestracao.etapas_da_execucao
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW WHEN (NEW.id_plano IS NOT NULL)
                    EXECUTE FUNCTION orquestracao.conferir_unidade_do_plano();

                -- Dependências em ordem topológica: a requerida vem antes na numeração (ciclo impossível também no banco).
                -- Só compara etapas da própria execução; etapa de outra execução continua recusada pela FK composta.
                CREATE FUNCTION orquestracao.conferir_ordem_da_dependencia() RETURNS trigger
                LANGUAGE plpgsql AS $$
                DECLARE
                    dependente integer;
                    requerida integer;
                BEGIN
                    SELECT numero INTO dependente FROM orquestracao.etapas_da_execucao
                     WHERE id = NEW.id_etapa AND id_execucao = NEW.id_execucao;
                    SELECT numero INTO requerida FROM orquestracao.etapas_da_execucao
                     WHERE id = NEW.id_etapa_requerida AND id_execucao = NEW.id_execucao;
                    IF dependente IS NOT NULL AND requerida IS NOT NULL AND requerida >= dependente THEN
                        RAISE EXCEPTION 'a etapa requerida precisa vir antes da dependente' USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER trg_dependencias_de_etapa_ordem BEFORE INSERT OR UPDATE ON orquestracao.dependencias_de_etapa
                    FOR EACH ROW EXECUTE FUNCTION orquestracao.conferir_ordem_da_dependencia();

                CREATE OR REPLACE FUNCTION orquestracao.conferir_delegacao() RETURNS trigger
                LANGUAGE plpgsql AS $$
                DECLARE
                    subordinada orquestracao.execucoes%ROWTYPE;
                    origem orquestracao.execucoes%ROWTYPE;
                    etapa orquestracao.etapas_da_execucao%ROWTYPE;
                    coordenador text;
                BEGIN
                    IF NEW.estado <> 0 OR NEW.motivo IS NOT NULL OR NEW.aceita_em IS NOT NULL OR NEW.iniciada_em IS NOT NULL
                       OR NEW.encerrada_em IS NOT NULL THEN
                        RAISE EXCEPTION 'delegação nasce solicitada' USING ERRCODE = 'check_violation';
                    END IF;
                    SELECT * INTO subordinada FROM orquestracao.execucoes WHERE id = NEW.id_execucao_subordinada;
                    SELECT * INTO origem FROM orquestracao.execucoes WHERE id = NEW.id_execucao_origem;
                    IF subordinada.id IS NULL OR origem.id IS NULL THEN
                        RAISE EXCEPTION 'execução da delegação inexistente' USING ERRCODE = 'foreign_key_violation';
                    END IF;
                    IF subordinada.tipo <> 3 OR subordinada.id_execucao_pai <> origem.id
                       OR subordinada.id_execucao_raiz <> NEW.id_execucao_raiz
                       OR subordinada.id_versao_do_agente IS DISTINCT FROM NEW.id_versao_destinataria
                       OR origem.id_versao_do_agente IS DISTINCT FROM NEW.id_versao_solicitante
                       OR cardinality(NEW.cadeia_de_agentes) <> subordinada.profundidade THEN
                        RAISE EXCEPTION 'delegação fora da hierarquia da execução subordinada' USING ERRCODE = 'check_violation';
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM orquestracao.versoes_de_agente v WHERE v.id = NEW.id_versao_solicitante
                                   AND v.id_agente = NEW.id_agente_solicitante
                                   AND v.hash_da_configuracao = NEW.hash_da_versao_solicitante)
                       OR NOT EXISTS (SELECT 1 FROM orquestracao.versoes_de_agente v WHERE v.id = NEW.id_versao_destinataria
                                   AND v.id_agente = NEW.id_agente_destinatario
                                   AND v.hash_da_configuracao = NEW.hash_da_versao_destinataria) THEN
                        RAISE EXCEPTION 'versões da delegação não conferem' USING ERRCODE = 'check_violation';
                    END IF;
                    -- Unidade de plano (AD-75, #310): a origem é uma Coordenada, a etapa é a unidade de um plano aprovado
                    -- dela, o destinatário é o participante do papel da unidade no snapshot (versão e hash congelados), o
                    -- solicitante é o coordenador congelado, o contrato é consulta.v1 e a versão destinatária tem a
                    -- capacidade dele. Nada disso vem do agente.
                    IF origem.tipo = 4 THEN
                        SELECT * INTO etapa FROM orquestracao.etapas_da_execucao
                         WHERE id = NEW.id_etapa_origem AND id_execucao = origem.id;
                        SELECT c.papel_do_coordenador INTO coordenador FROM orquestracao.coordenacoes_da_execucao c
                         WHERE c.id_execucao = origem.id;
                        IF etapa.id IS NULL OR etapa.tipo <> 3 OR etapa.id_plano IS NULL OR etapa.escrita
                           OR NEW.contrato <> 'consulta.v1' THEN
                            RAISE EXCEPTION 'delegação de coordenação só de unidade de plano, por consulta.v1'
                                USING ERRCODE = 'check_violation';
                        END IF;
                        IF NOT EXISTS (SELECT 1 FROM orquestracao.planos_da_coordenacao p
                                       WHERE p.id = etapa.id_plano AND p.id_execucao = origem.id AND p.estado = 1) THEN
                            RAISE EXCEPTION 'unidade de plano não aprovado' USING ERRCODE = 'check_violation';
                        END IF;
                        IF coordenador IS NULL OR etapa.papel = coordenador
                           OR etapa.id_versao_do_agente IS DISTINCT FROM NEW.id_versao_destinataria
                           OR NOT EXISTS (SELECT 1 FROM orquestracao.participantes_da_execucao pe
                                          WHERE pe.id_execucao = origem.id AND pe.papel = etapa.papel
                                            AND pe.id_versao_do_agente = NEW.id_versao_destinataria
                                            AND pe.hash_da_configuracao_do_agente = NEW.hash_da_versao_destinataria)
                           OR NOT EXISTS (SELECT 1 FROM orquestracao.participantes_da_execucao pe
                                          WHERE pe.id_execucao = origem.id AND pe.papel = coordenador
                                            AND pe.id_versao_do_agente = NEW.id_versao_solicitante
                                            AND pe.hash_da_configuracao_do_agente = NEW.hash_da_versao_solicitante) THEN
                            RAISE EXCEPTION 'delegação de coordenação fora do snapshot da execução'
                                USING ERRCODE = 'check_violation';
                        END IF;
                        IF NOT EXISTS (SELECT 1 FROM orquestracao.versoes_de_agente v WHERE v.id = NEW.id_versao_destinataria
                                       AND v.capacidades && ARRAY['Pesquisar', 'Revisar']::text[]) THEN
                            RAISE EXCEPTION 'destinatário sem capacidade para consulta.v1' USING ERRCODE = 'check_violation';
                        END IF;
                    END IF;
                    RETURN NEW;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_dependencias_de_etapa_ordem ON orquestracao.dependencias_de_etapa;
                DROP FUNCTION IF EXISTS orquestracao.conferir_ordem_da_dependencia();
                DROP TRIGGER IF EXISTS trg_etapas_da_execucao_unidade_do_plano ON orquestracao.etapas_da_execucao;
                DROP FUNCTION IF EXISTS orquestracao.conferir_unidade_do_plano();
                DROP TRIGGER IF EXISTS trg_etapas_da_execucao_unidade_imutavel ON orquestracao.etapas_da_execucao;
                DROP FUNCTION IF EXISTS orquestracao.proteger_unidade_do_plano();
                DROP TRIGGER IF EXISTS trg_pedidos_de_aprovacao_conferidos ON orquestracao.pedidos_de_aprovacao;
                DROP FUNCTION IF EXISTS orquestracao.conferir_pedido_de_aprovacao();
                DROP TRIGGER IF EXISTS trg_planos_da_coordenacao_decisao ON orquestracao.planos_da_coordenacao;
                DROP FUNCTION IF EXISTS orquestracao.conferir_decisao_do_plano();
                DROP TRIGGER IF EXISTS trg_planos_da_coordenacao_protegidos ON orquestracao.planos_da_coordenacao;
                DROP FUNCTION IF EXISTS orquestracao.proteger_plano_da_coordenacao();
                DROP TRIGGER IF EXISTS trg_coordenacoes_da_execucao_snapshot ON orquestracao.coordenacoes_da_execucao;
                DROP FUNCTION IF EXISTS orquestracao.proteger_coordenacao_da_execucao();
                DROP TRIGGER IF EXISTS trg_participantes_de_grupo_coordenado ON orquestracao.participantes_de_grupo;
                DROP FUNCTION IF EXISTS orquestracao.conferir_participante_de_grupo_coordenado();
                CREATE OR REPLACE FUNCTION orquestracao.proteger_pedido_de_aprovacao() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'pedidos de aprovação não são removidos' USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF OLD.estado <> 0 THEN
                        RAISE EXCEPTION 'pedido de aprovação já decidido' USING ERRCODE = 'restrict_violation';
                    END IF;
                    IF (NEW.id_execucao, NEW.id_etapa, NEW.rodada, NEW.hash_do_snapshot, NEW.hash_do_artefato, NEW.criado_em)
                       IS DISTINCT FROM (OLD.id_execucao, OLD.id_etapa, OLD.rodada, OLD.hash_do_snapshot, OLD.hash_do_artefato, OLD.criado_em) THEN
                        RAISE EXCEPTION 'o artefato de um pedido de aprovação é imutável' USING ERRCODE = 'restrict_violation';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE OR REPLACE FUNCTION orquestracao.conferir_delegacao() RETURNS trigger
                LANGUAGE plpgsql AS $$
                DECLARE
                    subordinada orquestracao.execucoes%ROWTYPE;
                    origem orquestracao.execucoes%ROWTYPE;
                BEGIN
                    IF NEW.estado <> 0 OR NEW.motivo IS NOT NULL OR NEW.aceita_em IS NOT NULL OR NEW.iniciada_em IS NOT NULL
                       OR NEW.encerrada_em IS NOT NULL THEN
                        RAISE EXCEPTION 'delegação nasce solicitada' USING ERRCODE = 'check_violation';
                    END IF;
                    SELECT * INTO subordinada FROM orquestracao.execucoes WHERE id = NEW.id_execucao_subordinada;
                    SELECT * INTO origem FROM orquestracao.execucoes WHERE id = NEW.id_execucao_origem;
                    IF subordinada.id IS NULL OR origem.id IS NULL THEN
                        RAISE EXCEPTION 'execução da delegação inexistente' USING ERRCODE = 'foreign_key_violation';
                    END IF;
                    IF subordinada.tipo <> 3 OR subordinada.id_execucao_pai <> origem.id
                       OR subordinada.id_execucao_raiz <> NEW.id_execucao_raiz
                       OR subordinada.id_versao_do_agente IS DISTINCT FROM NEW.id_versao_destinataria
                       OR origem.id_versao_do_agente IS DISTINCT FROM NEW.id_versao_solicitante
                       OR cardinality(NEW.cadeia_de_agentes) <> subordinada.profundidade THEN
                        RAISE EXCEPTION 'delegação fora da hierarquia da execução subordinada' USING ERRCODE = 'check_violation';
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM orquestracao.versoes_de_agente v WHERE v.id = NEW.id_versao_solicitante
                                   AND v.id_agente = NEW.id_agente_solicitante
                                   AND v.hash_da_configuracao = NEW.hash_da_versao_solicitante)
                       OR NOT EXISTS (SELECT 1 FROM orquestracao.versoes_de_agente v WHERE v.id = NEW.id_versao_destinataria
                                   AND v.id_agente = NEW.id_agente_destinatario
                                   AND v.hash_da_configuracao = NEW.hash_da_versao_destinataria) THEN
                        RAISE EXCEPTION 'versões da delegação não conferem' USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NEW;
                END $$;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_etapas_da_execucao_planos_da_coordenacao_id_plano",
                schema: "orquestracao",
                table: "etapas_da_execucao");

            migrationBuilder.DropForeignKey(
                name: "FK_pedidos_de_aprovacao_planos_da_coordenacao_id_plano",
                schema: "orquestracao",
                table: "pedidos_de_aprovacao");

            migrationBuilder.DropTable(
                name: "coordenacoes_da_execucao",
                schema: "orquestracao");

            migrationBuilder.DropTable(
                name: "planos_da_coordenacao",
                schema: "orquestracao");

            migrationBuilder.DropCheckConstraint(
                name: "ck_versoes_de_grupo_coordenador",
                schema: "orquestracao",
                table: "versoes_de_grupo");

            migrationBuilder.DropIndex(
                name: "IX_pedidos_de_aprovacao_id_plano",
                schema: "orquestracao",
                table: "pedidos_de_aprovacao");

            migrationBuilder.DropCheckConstraint(
                name: "ck_pedidos_de_aprovacao_finalidade",
                schema: "orquestracao",
                table: "pedidos_de_aprovacao");

            migrationBuilder.DropCheckConstraint(
                name: "ck_execucoes_roteiro_com_grupo",
                schema: "orquestracao",
                table: "execucoes");

            migrationBuilder.DropIndex(
                name: "IX_etapas_da_execucao_id_plano_unidade",
                schema: "orquestracao",
                table: "etapas_da_execucao");

            migrationBuilder.DropCheckConstraint(
                name: "ck_etapas_da_execucao_unidade_do_plano",
                schema: "orquestracao",
                table: "etapas_da_execucao");

            migrationBuilder.DropColumn(
                name: "papel_do_coordenador",
                schema: "orquestracao",
                table: "versoes_de_grupo");

            migrationBuilder.DropColumn(
                name: "finalidade",
                schema: "orquestracao",
                table: "pedidos_de_aprovacao");

            migrationBuilder.DropColumn(
                name: "id_plano",
                schema: "orquestracao",
                table: "pedidos_de_aprovacao");

            migrationBuilder.DropColumn(
                name: "id_plano",
                schema: "orquestracao",
                table: "etapas_da_execucao");

            migrationBuilder.DropColumn(
                name: "unidade",
                schema: "orquestracao",
                table: "etapas_da_execucao");

            migrationBuilder.AddCheckConstraint(
                name: "ck_execucoes_roteiro_com_grupo",
                schema: "orquestracao",
                table: "execucoes",
                sql: "(tipo = 2) = (id_versao_do_grupo IS NOT NULL) AND (tipo <> 2 OR id_versao_do_agente IS NULL)");
        }
    }
}
