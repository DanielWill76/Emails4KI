using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MimeKit;
using MimeKit.Text;

public class MailContent
{
    public string Subject { get; set; } = "";
    public DateTimeOffset? Date { get; set; }
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Cc { get; set; } = "";
    public string Body { get; set; } = "";
}

public static class MailToTextConverter
{
    private static readonly byte[] OleSignature = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };

    public static MailContent Read(string path)
    {
        return IsMsg(path) ? ReadMsg(path) : ReadEml(path);
    }

    public static void WriteText(string sourceMail, string targetTxt, bool utf8Bom)
    {
        var content = Read(sourceMail);
        File.WriteAllText(targetTxt, Format(content), new UTF8Encoding(utf8Bom));
    }

    public static string Format(MailContent mail)
    {
        var sb = new StringBuilder();
        sb.Append("Von: ").AppendLine(mail.From);
        sb.Append("An: ").AppendLine(mail.To);
        if (!string.IsNullOrWhiteSpace(mail.Cc))
            sb.Append("Cc: ").AppendLine(mail.Cc);
        sb.Append("Datum: ").AppendLine(mail.Date?.ToString("dd.MM.yyyy HH:mm") ?? "");
        sb.Append("Betreff: ").AppendLine(mail.Subject);
        sb.AppendLine();
        sb.AppendLine(NormalizeBody(mail.Body));
        return sb.ToString();
    }

    private static bool IsMsg(string path)
    {
        if (string.Equals(Path.GetExtension(path), ".msg", StringComparison.OrdinalIgnoreCase))
            return true;

        // Dateiendung kann fehlen oder falsch sein: OLE-Signatur prüfen
        var header = new byte[OleSignature.Length];
        using var fs = File.OpenRead(path);
        int read = fs.Read(header, 0, header.Length);
        return read == header.Length && header.SequenceEqual(OleSignature);
    }

    private static MailContent ReadEml(string path)
    {
        var message = MimeMessage.Load(path);

        string body = message.TextBody ?? "";
        if (string.IsNullOrWhiteSpace(body) && !string.IsNullOrWhiteSpace(message.HtmlBody))
            body = new HtmlToText().Convert(message.HtmlBody);

        return new MailContent
        {
            Subject = message.Subject ?? "",
            Date = message.Date == DateTimeOffset.MinValue ? null : message.Date,
            From = FormatAddresses(message.From),
            To = FormatAddresses(message.To),
            Cc = FormatAddresses(message.Cc),
            Body = body
        };
    }

    private static MailContent ReadMsg(string path)
    {
        using var msg = new MsgReader.Outlook.Storage.Message(path);

        string body = msg.BodyText ?? "";
        if (string.IsNullOrWhiteSpace(body) && !string.IsNullOrWhiteSpace(msg.BodyHtml))
            body = new HtmlToText().Convert(msg.BodyHtml);

        return new MailContent
        {
            Subject = msg.Subject ?? "",
            Date = msg.SentOn,
            From = msg.GetEmailSender(false, false) ?? "",
            To = msg.GetEmailRecipients(MsgReader.Outlook.RecipientType.To, false, false) ?? "",
            Cc = msg.GetEmailRecipients(MsgReader.Outlook.RecipientType.Cc, false, false) ?? "",
            Body = body
        };
    }

    private static string FormatAddresses(InternetAddressList list)
    {
        return string.Join("; ", list.Mailboxes.Select(m =>
            string.IsNullOrWhiteSpace(m.Name) ? m.Address : $"{m.Name} <{m.Address}>"));
    }

    private static string NormalizeBody(string body)
    {
        return body.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", Environment.NewLine).TrimEnd();
    }
}

