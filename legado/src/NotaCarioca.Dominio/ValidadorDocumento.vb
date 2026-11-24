Imports System
Imports System.Text

Public Class ValidadorDocumento

    Public Shared Function SomenteDigitos(ByVal doc As String) As String
        If doc Is Nothing Then Return ""
        Dim sb As New StringBuilder()
        For Each c As Char In doc
            If Char.IsDigit(c) Then sb.Append(c)
        Next
        Return sb.ToString()
    End Function

    Public Shared Function ParaNumero(ByVal doc As String) As Long
        Dim d As String = SomenteDigitos(doc)
        If d = "" Then Return 0
        Return CLng(d)
    End Function

    Public Shared Function ValidarCpf(ByVal doc As String) As Boolean
        Dim d As String = SomenteDigitos(doc)
        If d.Length <> 11 Then Return False
        If Not IsNumeric(d) Then Return False
        If TodosIguais(d) Then Return False

        Dim soma As Integer = 0
        For i As Integer = 0 To 8
            soma += CInt(d.Substring(i, 1)) * (10 - i)
        Next
        Dim dv1 As Integer = (soma * 10) Mod 11
        If dv1 = 10 Then dv1 = 0
        If dv1 <> CInt(d.Substring(9, 1)) Then Return False

        soma = 0
        For i As Integer = 0 To 9
            soma += CInt(d.Substring(i, 1)) * (11 - i)
        Next
        Dim dv2 As Integer = (soma * 10) Mod 11
        If dv2 = 10 Then dv2 = 0
        Return dv2 = CInt(d.Substring(10, 1))
    End Function

    ''' <summary>
    ''' Converte caractere alfanumérico em seu valor numérico para cálculo de dígitos verificadores de CNPJ alfanumérico
    ''' Mapeamento: 0-9 = 0-9, A=10, B=11, C=12, ..., Z=35 (pulando I, O, U)
    ''' </summary>
    Private Shared Function ConverterCaracterAlfanumericoParaValor(ByVal c As Char) As Integer
        If Char.IsDigit(c) Then
            Return AscW(c) - AscW("0"c)
        Else
            ' Normalizar para maiúscula
            Dim cUpper As Char = Char.ToUpper(c)
            ' Mapeamento: A=10, B=11, ..., H=17 (8 caracteres)
            '            J=18, K=19, ..., N=22 (5 caracteres, pulou I)
            '            P=23, Q=24, R=25, S=26, T=27 (5 caracteres, pulou O)
            '            V=28, W=29, X=30, Y=31, Z=32 (5 caracteres, pulou U)
            If cUpper >= "A"c AndAlso cUpper <= "H"c Then
                Return AscW(cUpper) - AscW("A"c) + 10
            ElseIf cUpper >= "J"c AndAlso cUpper <= "N"c Then
                Return AscW(cUpper) - AscW("J"c) + 18
            ElseIf cUpper >= "P"c AndAlso cUpper <= "T"c Then
                Return AscW(cUpper) - AscW("P"c) + 23
            ElseIf cUpper >= "V"c AndAlso cUpper <= "Z"c Then
                Return AscW(cUpper) - AscW("V"c) + 28
            Else
                ' Caractere inválido (I, O, U, ou outro fora do alfabeto)
                Return -1
            End If
        End If
    End Function

    ''' <summary>
    ''' Valida CNPJ que pode ser:
    ''' 1. Numérico puro: 14 dígitos (formato tradicional)
    ''' 2. Alfanumérico: 8 caracteres (alfanuméricos) + 4 dígitos (filial) + 2 dígitos (verificadores)
    '''    Desde julho/2026 (IN RFB nº 2.229/2024)
    ''' </summary>
    Public Shared Function ValidarCnpj(ByVal doc As String) As Boolean
        If doc Is Nothing Then Return False

        ' Remover caracteres de formatação e espaços
        Dim d As String = doc.Trim().Replace(".", "").Replace("/", "").Replace("-", "").Replace(" ", "")
        If d.Length <> 14 Then Return False

        ' Verificar se é CNPJ numérico (todos os 14 caracteres são dígitos)
        If IsNumeric(d) Then
            Return ValidarCnpjNumerico(d)
        Else
            ' Verificar se é CNPJ alfanumérico (primeiros 8 podem ser alfanuméricos, resto é numérico)
            Return ValidarCnpjAlfanumerico(d)
        End If
    End Function

    Private Shared Function ValidarCnpjNumerico(ByVal d As String) As Boolean
        ' Validação de CNPJ numérico puro (implementação existente)
        If CLng(d) = 0 Then Return False
        If TodosIguais(d) Then Return False

        Dim pesos1() As Integer = {5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2}
        Dim pesos2() As Integer = {6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2}

        Dim soma As Integer = 0
        For i As Integer = 0 To 11
            soma += CInt(d.Substring(i, 1)) * pesos1(i)
        Next
        Dim resto As Integer = soma Mod 11
        Dim dv1 As Integer = If(resto < 2, 0, 11 - resto)
        If dv1 <> CInt(d.Substring(12, 1)) Then Return False

        soma = 0
        For i As Integer = 0 To 12
            soma += CInt(d.Substring(i, 1)) * pesos2(i)
        Next
        resto = soma Mod 11
        Dim dv2 As Integer = If(resto < 2, 0, 11 - resto)
        Return dv2 = CInt(d.Substring(13, 1))
    End Function

    Private Shared Function ValidarCnpjAlfanumerico(ByVal d As String) As Boolean
        ' Validação de CNPJ alfanumérico (IN RFB nº 2.229/2024)
        ' Formato: 8 alfanuméricos + 4 numéricos + 2 dígitos verificadores
        ' Verificar se tem 8 primeiros caracteres e 6 últimos são dígitos

        ' Primeiros 8 devem ser alfanuméricos (pode ter letras)
        Dim primeirosParte As String = d.Substring(0, 8)
        Dim filialParte As String = d.Substring(8, 4)
        Dim verificadores As String = d.Substring(12, 2)

        ' Validar que partes numéricos são realmente numéricos
        If Not IsNumeric(filialParte) OrElse Not IsNumeric(verificadores) Then
            Return False
        End If

        ' Validar caracteres inválidos (I, O, U não são permitidos)
        For Each c As Char In primeirosParte
            If Char.IsLetter(c) Then
                Dim cUpper As Char = Char.ToUpper(c)
                If cUpper = "I"c OrElse cUpper = "O"c OrElse cUpper = "U"c Then
                    Return False
                End If
            End If
        Next

        ' Verificar que todos os primeiros 8 caracteres são válidos (0-9, A-Z exceto I, O, U)
        Dim valores(13) As Integer
        For i As Integer = 0 To 11
            Dim val As Integer = ConverterCaracterAlfanumericoParaValor(d(i))
            If val = -1 Then Return False
            valores(i) = val
        Next

        ' Converter os últimos dois dígitos
        valores(12) = AscW(d(12)) - AscW("0"c)
        valores(13) = AscW(d(13)) - AscW("0"c)

        ' Validar sequência de todos iguais
        If TodosIguaisAlfanumerico(d) Then Return False

        ' Calcular primeiro dígito verificador
        Dim pesos1() As Integer = {5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2}
        Dim soma As Integer = 0
        For i As Integer = 0 To 11
            soma += valores(i) * pesos1(i)
        Next
        Dim resto As Integer = soma Mod 11
        Dim dv1 As Integer = If(resto < 2, 0, 11 - resto)

        If dv1 <> valores(12) Then Return False

        ' Calcular segundo dígito verificador
        Dim pesos2() As Integer = {6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2}
        soma = 0
        For i As Integer = 0 To 12
            soma += valores(i) * pesos2(i)
        Next
        resto = soma Mod 11
        Dim dv2 As Integer = If(resto < 2, 0, 11 - resto)

        Return dv2 = valores(13)
    End Function

    Private Shared Function TodosIguaisAlfanumerico(ByVal d As String) As Boolean
        ' Comparar todos os caracteres (antes de conversão)
        For i As Integer = 1 To d.Length - 1
            If Char.ToUpper(d(i)) <> Char.ToUpper(d(0)) Then Return False
        Next
        Return True
    End Function

    Public Shared Function ValidarCpfOuCnpj(ByVal doc As String) As Boolean
        If doc Is Nothing Then Return False

        ' Remover caracteres de formatação para contar o tamanho efetivo
        Dim d As String = doc.Trim().Replace(".", "").Replace("/", "").Replace("-", "").Replace(" ", "")

        ' CNPJ alfanumérico pode ter letras, então não podemos usar SomenteDigitos
        ' Vamos contar caracteres e validar

        If d.Length = 11 Then
            ' Pode ser CPF (11 dígitos)
            Dim soDigitos As String = SomenteDigitos(doc)
            If soDigitos.Length = 11 Then
                Return ValidarCpf(soDigitos)
            End If
        End If

        If d.Length = 14 Then
            ' Pode ser CNPJ (numérico ou alfanumérico)
            Return ValidarCnpj(d)
        End If

        Return False
    End Function

    Private Shared Function TodosIguais(ByVal d As String) As Boolean
        For i As Integer = 1 To d.Length - 1
            If d(i) <> d(0) Then Return False
        Next
        Return True
    End Function

End Class
