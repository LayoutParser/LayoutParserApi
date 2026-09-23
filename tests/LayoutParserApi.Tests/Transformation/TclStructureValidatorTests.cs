using LayoutParserApi.Services.Transformation;

namespace LayoutParserApi.Tests.Transformation
{
    /// <summary>
    /// Issue #173 — o TODO "Implementar validação mais detalhada" em
    /// <c>TransformationValidatorService.ValidateTclAsync</c> virou esta checagem de estrutura
    /// (MAP/LINE/FIELD + atributos mínimos), compartilhada com <c>ImprovedTclGeneratorService</c>.
    /// </summary>
    public class TclStructureValidatorTests
    {
        [Fact]
        public void Tcl_valido_com_map_line_field_passa()
        {
            var tcl =
                "<MAP>" +
                "  <LINE identifier=\"HEADER\" name=\"HEADER\">" +
                "    <FIELD name=\"data\" length=\"8\"/>" +
                "  </LINE>" +
                "</MAP>";

            var result = TclStructureValidator.Validate(tcl);

            Assert.True(result.Success);
            Assert.Empty(result.Errors);
        }

        [Fact]
        public void Tcl_sem_elemento_map_falha()
        {
            var tcl = "<ROOT><LINE name=\"HEADER\"><FIELD name=\"x\" length=\"1\"/></LINE></ROOT>";

            var result = TclStructureValidator.Validate(tcl);

            Assert.False(result.Success);
            Assert.Contains(result.Errors, e => e.Contains("MAP não encontrado"));
        }

        [Fact]
        public void Tcl_sem_nenhuma_line_falha()
        {
            var tcl = "<MAP></MAP>";

            var result = TclStructureValidator.Validate(tcl);

            Assert.False(result.Success);
            Assert.Contains(result.Errors, e => e.Contains("Nenhuma linha"));
        }

        [Fact]
        public void Line_sem_nenhum_field_falha()
        {
            var tcl = "<MAP><LINE name=\"HEADER\"></LINE></MAP>";

            var result = TclStructureValidator.Validate(tcl);

            Assert.False(result.Success);
            Assert.Contains(result.Errors, e => e.Contains("não tem campos"));
        }

        [Fact]
        public void Field_sem_atributo_name_falha()
        {
            var tcl = "<MAP><LINE name=\"HEADER\"><FIELD length=\"8\"/></LINE></MAP>";

            var result = TclStructureValidator.Validate(tcl);

            Assert.False(result.Success);
            Assert.Contains(result.Errors, e => e.Contains("sem atributo 'name'"));
        }

        [Fact]
        public void Field_sem_atributo_length_falha()
        {
            var tcl = "<MAP><LINE name=\"HEADER\"><FIELD name=\"data\"/></LINE></MAP>";

            var result = TclStructureValidator.Validate(tcl);

            Assert.False(result.Success);
            Assert.Contains(result.Errors, e => e.Contains("não tem atributo 'length'"));
        }

        [Fact]
        public void Field_nao_exige_atributo_start_formato_real_e_posicional_por_length_apenas()
        {
            // Regressão: o formato real de produção (ver TransformationPipelineServiceMapFileTests)
            // não tem "start"/"offset" — só "name"+"length". Exigir "start" quebraria TCLs válidos.
            var tcl = "<MAP><LINE name=\"HEADER\"><FIELD name=\"data\" length=\"8\"/></LINE></MAP>";

            var result = TclStructureValidator.Validate(tcl);

            Assert.True(result.Success);
        }

        [Fact]
        public void Xml_malformado_falha_com_mensagem_especifica()
        {
            var tcl = "<MAP><LINE name=\"HEADER\">";

            var result = TclStructureValidator.Validate(tcl);

            Assert.False(result.Success);
            Assert.Contains(result.Errors, e => e.Contains("Erro ao validar estrutura do TCL"));
        }
    }
}
