using LayoutParserApi.Services.Interfaces;

using Microsoft.AspNetCore.Mvc;

using System.Text;

namespace LayoutParserApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class TestController : ControllerBase
    {
        private readonly ILayoutParserService _parserService;

        public TestController(ILayoutParserService parserService)
        {
            _parserService = parserService;
        }

        [HttpPost("parse-with-sample")]
        public async Task<IActionResult> ParseWithSampleLayout(IFormFile txtFile)
        {
            if (txtFile == null)
                return BadRequest("Arquivo TXT é obrigatório");

            // XML de exemplo hardcoded (use o seu XML real)
            var sampleXml = @"<?xml version=""1.0"" encoding=""utf-16""?>
<LayoutVO xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xsi:type=""TextLayoutVO"">
    <LayoutGuid>LAY_e339073e-32d1-492e-ae8a-dcf6337b21a1</LayoutGuid>
    <LayoutType>TextPositional</LayoutType>
    <Name>LAY_CNHI_TXT_MQSERIES_ENVNFE_4.00_NFe</Name>
    <Description>LAY_CNHI_TXT_MQSERIES_ENVNFE_4.00_NFe</Description>
    <LimitOfCaracters>0</LimitOfCaracters>
    <Elements>
        <Element xsi:type=""LineElementVO"">
            <ElementGuid>LIN_e7cc6ed2-4fbe-4dc8-9561-189a94e1b6fe</ElementGuid>
            <Description/>
            <Sequence>1</Sequence>
            <Name>HEADER</Name>
            <IsRequired>false</IsRequired>
            <Elements>
                <Element xsi:type=""FieldElementVO"">
                    <ElementGuid>FLD_2e97cfe7-8c5e-4da7-af48-a1f49b313b13</ElementGuid>
                    <Description/>
                    <Sequence>1</Sequence>
                    <Name>Data</Name>
                    <IsRequired>false</IsRequired>
                    <StartValue>1</StartValue>
                    <IncrementValue>1</IncrementValue>
                    <LengthField>8</LengthField>
                    <AlignmentType>Left</AlignmentType>
                    <IsStaticValue>false</IsStaticValue>
                    <IsCaseSensitiveValue>false</IsCaseSensitiveValue>
                    <IsSequential>false</IsSequential>
                    <RemoveWhiteSpaceType>All</RemoveWhiteSpaceType>
                    <DataTypeGuid>DAT_ea51dfb4-e8db-4f52-813e-0eec16a06653</DataTypeGuid>
                </Element>
            </Elements>
            <MinimalOccurrence>1</MinimalOccurrence>
            <MaximumOccurrence>1</MaximumOccurrence>
            <InitialValue>HEADER</InitialValue>
            <IsToValidateLengthCharacters>false</IsToValidateLengthCharacters>
            <IsToValidateFieldLesserLength>false</IsToValidateFieldLesserLength>
            <IsPositionalGroupRepetition>false</IsPositionalGroupRepetition>
            <NotRealizeParser>false</NotRealizeParser>
        </Element>
    </Elements>
    <Delimiter>0</Delimiter>
    <Escape xsi:nil=""true""/>
    <InitializerLine/>
    <FinisherLine/>
    <WithBreakLines>false</WithBreakLines>
</LayoutVO>";

            using var xmlStream = new MemoryStream(Encoding.UTF8.GetBytes(sampleXml));
            using var txtStream = txtFile.OpenReadStream();

            var result = await _parserService.ParseAsync(xmlStream, txtStream);

            if (!result.Success)
                return BadRequest(result.ErrorMessage);

            return Ok(new
            {
                success = true,
                fields = result.ParsedFields,
                text = result.RawText,
                summary = result.Summary
            });
        }

        [HttpGet("health")]
        public IActionResult HealthCheck()
        {
            return Ok(new
            {
                status = "API está funcionando",
                timestamp = DateTime.Now
            });
        }
    }
}