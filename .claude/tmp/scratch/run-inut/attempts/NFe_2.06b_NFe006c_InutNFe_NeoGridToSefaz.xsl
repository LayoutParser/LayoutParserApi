<?xml version='1.0' encoding='ISO-8859-1' ?>
<xsl:stylesheet exclude-result-prefixes="ng" version="1.0" extension-element-prefixes="ng" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:ng="com.neogrid.integrator.XSLFunctions">
	<xsl:output method="xml" encoding="UTF-8"/>
	<infInut>
		<xsl:attribute name="Id"><xsl:value-of select="concat('ID',ROOT/Header/cUF,ROOT/Header/ano,ROOT/Header/CNPJ,ROOT/Header/mod,format-number(ROOT/Header/serie,'000'),format-number(ROOT/Header/nNFIni,'000000000'),format-number(ROOT/Header/nNFFin,'000000000'))"/></xsl:attribute>
		<tpAmb>
			<xsl:value-of select="ROOT/Header/tpAmb"/>
		</tpAmb>
		<xServ>INUTILIZAR</xServ>
		<cUF>
			<xsl:value-of select="ROOT/Header/cUF"/>
		</cUF>
		<ano>
			<xsl:value-of select="ROOT/Header/ano"/>
		</ano>
		<CNPJ>
			<xsl:value-of select="ROOT/Header/CNPJ"/>
		</CNPJ>
		<mod>
			<xsl:value-of select="ROOT/Header/mod"/>
		</mod>
		<serie>
			<xsl:value-of select="number(ROOT/Header/serie)"/>
		</serie>
		<nNFIni>
			<xsl:value-of select="number(ROOT/Header/nNFIni)"/>
		</nNFIni>
		<nNFFin>
			<xsl:value-of select="number(ROOT/Header/nNFFin)"/>
		</nNFFin>
		<xJust>
			<xsl:value-of select="ROOT/Header/xJust"/>
		</xJust>
	</infInut>
</xsl:stylesheet>