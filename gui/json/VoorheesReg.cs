/*----------------------------------------------------------------------------*/
/* VoorheesReg.cs                                                             */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/*----------------------------------------------------------------------------*/
/* Windows registry files (.reg), as regedit writes and reads them.  What     */
/* follows was measured on real files written by "reg export" (as found:      */
/* regedit's own source is not in the tree):                                  */
/*     o UTF-16 LE with a byte order mark, CRLF;                              */
/*     o first line "Windows Registry Editor Version 5.00" (or "REGEDIT4",    */
/*       whose hex(2) and hex(7) bytes are ANSI, not UTF-16), a blank line;   */
/*     o "[path]" keys ("[-path]" deletes one), each followed by its values,  */
/*       a blank line after each key's values; the file ends with a blank     */
/*       line;                                                                */
/*     o values: name "@" (the default value) or "quoted" with \\ and \"      */
/*       escapes, "=", then "text" (REG_SZ), dword:8 hex digits, hex:bytes    */
/*       (REG_BINARY), hex(2): (EXPAND_SZ), hex(7): (MULTI_SZ), hex(b):       */
/*       (QWORD), any other hex(N):, or "-" (delete the value);               */
/*     o long hex values wrap: after a byte and its comma, once the line is   */
/*       longer than 76 characters, ",\" ends it and the next line starts     */
/*       with two spaces (MEASURED: continuation lines are 78 characters;     */
/*       first lines 79 or 80, depending on the name); strings never wrap.    */
/* ";" lines are comments.  No end-of-line comments.                          */
/*                                                                            */
/* Model, beyond the line engine's (VoorheesLines.cs header):                 */
/*     document: Vn_sRaw holds the header line (written first; not shown in   */
/*               the tree); its first item's lead holds the blank line.       */
/*     key     : VI_MEMBER keyed by the path (no "-"), value a VRG_KEY or     */
/*               VRG_DELETEKEY container; text after the "]" in Vi_sValueGap. */
/*     value   : VI_MEMBER keyed by the name ("" for "@"), Vi_sKeyRaw the     */
/*               quoted name as written when generation would differ,         */
/*               Vi_sKeyGap the "=" (and any spaces round it), value a leaf   */
/*               whose Vn_sRaw is the value exactly as written (hex           */
/*               continuation lines included) and whose Vn_sText is the       */
/*               Value box's text (strings as text, numbers as 0x...,         */
/*               binary as aa,bb pairs, MULTI_SZ one string a line).          */
/* Values outside any key, values under a key being deleted, and lines that   */
/* are not keys, values or comments, are refused with line and column; so is  */
/* a malformed value.                                                         */
/*                                                                            */
/* This editor never imports a .reg into the registry: it only reads and      */
/* writes the file.                                                           */
/*                                                                            */
/* Class:                                                                     */
/*     VoorheesReg (Vrg_) : static functions only; nothing is kept between    */
/*                          calls, so any of them may run on a worker thread. */
/*----------------------------------------------------------------------------*/

/* The framework parts used: the kind lists and byte lists                  */
/* (Collections.Generic), hex and invariant number reading and writing      */
/* (Globalization), and the StringBuilder and the string encodings of the   */
/* hex(2) / hex(7) data and of a new file (Text).                           */
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

/*----------------------------------------------------------------------------*/
/* VoorheesReg                                                                */
/*                                                                            */
/* Registry file lines, kinds and rules.                                      */
/*----------------------------------------------------------------------------*/
static class VoorheesReg
{
    /*------------------------------------------------------------------------*/
    /* REG's kinds of node, in REG's range (40-49): every format has its own  */
    /* decade, so one int names a kind in any document (VoorheesIni.cs says   */
    /* why).  VRG_INVALID sits outside the range on purpose: it is never a    */
    /* kind in a document.                                                    */
    /*------------------------------------------------------------------------*/

    public const int VRG_SZ = 40;                        // "text": REG_SZ
    public const int VRG_EXPANDSZ = 41;                  // hex(2): REG_EXPAND_SZ (UTF-16 text, ANSI in a REGEDIT4 file, with %VARS%)
    public const int VRG_MULTISZ = 42;                   // hex(7): REG_MULTI_SZ (a list of strings)
    public const int VRG_DWORD = 43;                     // dword: REG_DWORD (32 bits)
    public const int VRG_QWORD = 44;                     // hex(b): REG_QWORD (64 bits, little-endian bytes)
    public const int VRG_BINARY = 45;                    // hex: REG_BINARY
    public const int VRG_HEXOTHER = 46;                  // hex(N): any other type, kept as bytes
    public const int VRG_DELETEVALUE = 47;               // "name"=- : delete the value
    public const int VRG_KEY = 48;                       // [path]
    public const int VRG_DELETEKEY = 49;                 // [-path]: delete the key
    const int VRG_INVALID = 59;                          // a malformed value line (never in a document: Vrg_LineProblem refuses it)

    /*------------------------------------------------------------------------*/
    /* The file's furniture, and limits: the two header lines, the names new  */
    /* keys and values start from, and the wrapping a generated hex value is  */
    /* given (measured on real exports, as above).                            */
    /*------------------------------------------------------------------------*/

    public const string VRG_HEADER5 = "Windows Registry Editor Version 5.00";  // regedit 5's first line
    public const string VRG_HEADER4 = "REGEDIT4";                              // the old first line (ANSI strings)
    const string VRG_NEWKEYBASE = "HKEY_CURRENT_USER\\Software\\NewKey";       // a new key's path, before 1, 2, 3 ...
    const string VRG_NEWVALUEBASE = "NewValue";                                // a new value's name, likewise
    const int VRG_WRAPAFTER = 76;                                              // a hex line wraps once longer than this (measured)
    const string VRG_WRAPINDENT = "  ";                                        // a hex continuation line's indentation (measured)

    /* The five hives a key path may start with, full names only (a short     */
    /* form such as HKLM is not accepted here).  The order is the Add Key     */
    /* dialog's drop-down order, and an index into it is what Vrg_HiveIndex   */
    /* answers.                                                               */
    static readonly string[] VRG_HIVES = new string[]
    {
        "HKEY_LOCAL_MACHINE",                            // machine-wide settings
        "HKEY_CURRENT_USER",                             // the user's settings
        "HKEY_CLASSES_ROOT",                             // file types and COM
        "HKEY_USERS",                                    // every loaded user profile
        "HKEY_CURRENT_CONFIG"                            // the current hardware profile
    };

    /*------------------------------------------------------------------------*/
    /* Vrg_HeaderProblem:                                                     */
    /*                                                                        */
    /* Why a file's first line is not a registry file's header, if it is not: */
    /* it must be "Windows Registry Editor Version 5.00" or "REGEDIT4"        */
    /* (trailing spaces allowed, kept as written).  Called by the line engine */
    /* (VoorheesLines.cs, Vln_HeaderProblem) before any other line is read.   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sLine : the first line (without its line break).                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the reason, or null for a header.                         */
    /*------------------------------------------------------------------------*/
    public static string Vrg_HeaderProblem(string sLine)
    {
        string sTrim;                                    // the line without trailing spaces

        /* Trailing spaces and tabs are let through (the line itself is     */
        /* kept as written, Vln_ParseLines); the text before them must be   */
        /* one of the two headers exactly, case included.                   */
        sTrim = sLine.TrimEnd(' ', '\t');
        if (sTrim == VRG_HEADER5 || sTrim == VRG_HEADER4)
        {   /* A header: no objection. */
            return(null);
        }

        /* Anything else: not a registry file at all, said with both lines   */
        /* it could have had (the engine puts "Line 1, column 1" first).     */
        return("not a registry file: its first line must be \"" + VRG_HEADER5
            + "\" (or \"" + VRG_HEADER4 + "\")");
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_IsAnsi:                                                            */
    /*                                                                        */
    /* Whether a document is a REGEDIT4 file, whose hex(2) and hex(7)         */
    /* strings are ANSI bytes rather than UTF-16.                             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     root : the document node (its Vn_sRaw is the header).              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true for REGEDIT4.                                          */
    /*------------------------------------------------------------------------*/
    static bool Vrg_IsAnsi(VoorheesNode root)
    {
        /* The old header, judged as Vrg_HeaderProblem judges it (trailing   */
        /* spaces let through).  A document with no header line (null) is    */
        /* taken as regedit 5.                                               */
        return(root.Vn_sRaw != null
            && root.Vn_sRaw.TrimEnd(' ', '\t') == VRG_HEADER4);
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_ParseLine:                                                         */
    /*                                                                        */
    /* One line as an item: a ";" comment; a "[path]" or "[-path]" key (the   */
    /* path up to the LAST "]"); or a value: "@" or a quoted name, "=", and   */
    /* the value (Vrg_ParseValue).  A line that is none of these comes back   */
    /* as an entry with no key gap, and a malformed value as a VRG_INVALID    */
    /* leaf holding the reason; Vrg_LineProblem reports both.  Its one caller */
    /* is the line engine (VoorheesLines.cs, Vln_ParseLine), which relies on  */
    /* every character of the line landing in one of the item's fields.       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sContent : the line from its first non-space character to its line */
    /*                break (not empty).                                      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesItem : the item; its lead is the line engine's to set.     */
    /*------------------------------------------------------------------------*/
    public static VoorheesItem Vrg_ParseLine(string sContent)
    {
        VoorheesItem item;                               // the item made
        VoorheesNode node;                               // a key's container
        string sName;                                    // a value's decoded name
        string sNameRaw;                                 // its token as written
        int iClose;                                      // a key's "]"
        int iEquals;                                     // a value's "="
        int iValue;                                      // where the value starts

        if (sContent[0] == ';')
        {   /* A comment line, kept whole: ";" is regedit's only comment     */
            /* marker (a "#" line is not a comment here, and is refused as   */
            /* neither key, value nor comment).                              */
            return(VoorheesItem.Vi_NewComment(sContent));
        }

        if (sContent[0] == '[')
        {   /* A key, if there is a "]" -- the LAST one on the line, so a   */
            /* path may itself hold a "]".                                  */
            iClose = sContent.LastIndexOf(']');
            if (iClose > 1)
            {   /* [path] or [-path], with at least one character between    */
                /* the brackets ("[]" falls through and is refused below).   */
                if (sContent[1] == '-')
                {   /* Delete the key: the path starts after the "-", which   */
                    /* is the kind's mark, not part of the path.              */
                    node = VoorheesNode.Vn_NewContainer(VRG_DELETEKEY);
                    item = VoorheesItem.Vi_NewMember(sContent.Substring(2,
                        iClose - 2), node);
                }
                else
                {   /* The key: the path is everything between the brackets. */
                    node = VoorheesNode.Vn_NewContainer(VRG_KEY);
                    item = VoorheesItem.Vi_NewMember(sContent.Substring(1,
                        iClose - 1), node);
                }
                /* A key has no closing text: its last value's line break   */
                /* is the next item's lead (VoorheesLines.cs), so "" is     */
                /* stored, not left to be generated.                        */
                node.Vn_sClose = "";
                if (iClose + 1 < sContent.Length)
                {   /* Text after the "]" (spaces, or anything): kept in   */
                    /* Vi_sValueGap so Vrg_WriteHeader puts it back.       */
                    item.Vi_sValueGap = sContent.Substring(iClose + 1);
                }
                /* The key, empty until the line engine adds its values. */
                return(item);
            }
        }

        /* A value: its name first, either "@" (the default value, kept as   */
        /* the empty name) or a quoted token decoded.  iEquals stays -1      */
        /* until a name followed by "=" is found.                            */
        iEquals = -1;
        sName = null;
        sNameRaw = null;
        if (sContent[0] == '@')
        {   /* The default value: name "", token "@". */
            sName = "";
            sNameRaw = "@";
        }
        else if (sContent[0] == '"')
        {   /* A quoted name: the token through its closing quote. */
            sNameRaw = Vrg_QuotedToken(sContent, 0);
            if (sNameRaw != null)
            {   /* Closed: decode it.  An unclosed one leaves sNameRaw null,   */
                /* and the line is refused below.                              */
                sName = Vrg_Unquote(sNameRaw);
            }
        }

        if (sNameRaw != null)
        {   /* A name: then "=", spaces and tabs allowed before it. */
            iEquals = sNameRaw.Length;
            while (iEquals < sContent.Length && (sContent[iEquals] == ' '
                || sContent[iEquals] == '\t'))
            {   /* One space or tab before the "=" per iteration. */
                iEquals++;
            }
            if (iEquals >= sContent.Length || sContent[iEquals] != '=')
            {   /* No "=" after the name: not a value. */
                iEquals = -1;
            }
        }

        if (iEquals < 0)
        {   /* Not a key, a value or a comment.  It is still made an item --    */
            /* the whole line as its key, with no key gap -- because the line   */
            /* engine needs one; the missing gap is how Vrg_LineProblem knows   */
            /* to refuse it, so it never reaches a document.                    */
            return(VoorheesItem.Vi_NewMember(sContent,
                VoorheesNode.Vn_NewLeaf(VRG_SZ, "")));
        }

        /* The value starts after the "=" and any spaces or tabs after it. */
        iValue = iEquals + 1;
        while (iValue < sContent.Length && (sContent[iValue] == ' '
            || sContent[iValue] == '\t'))
        {   /* One space or tab after the "=" per iteration. */
            iValue++;
        }

        /* The member: its decoded name, and the value read from the rest   */
        /* of the line.  Everything between name and value -- the spaces    */
        /* and the "=" -- is kept whole in Vi_sKeyGap for the write.        */
        item = VoorheesItem.Vi_NewMember(sName, Vrg_ParseValue(
            sContent.Substring(iValue)));
        item.Vi_sKeyGap = sContent.Substring(sNameRaw.Length, iValue - sNameRaw.Length);
        if (sName.Length == 0 || sNameRaw != Vrg_QuoteName(sName))
        {   /* "@" (or a quoted empty name), or quoted in a way generation   */
            /* would not give back -- an escape Vrg_Quote would not write:   */
            /* the token is kept, so the line writes back as it was.         */
            item.Vi_sKeyRaw = sNameRaw;
        }

        /* Trailing spaces after a string: kept apart from it. */
        if (item.Vi_node.Vn_iKind == VRG_SZ && item.Vi_node.Vn_sRaw != null)
        {   /* The raw string ends at its closing quote; anything after it is the gap. */
            Vrg_SplitTrailing(item);
        }

        /* The value as written; every character of the line is in one of   */
        /* the item's fields.                                               */
        return(item);
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_SplitTrailing:                                                     */
    /*                                                                        */
    /* Moves what follows a string value's closing quote (spaces) from its    */
    /* raw text into the item's value gap.                                    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     item : the value just parsed (a VRG_SZ leaf with a raw token).     */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the raw ends at the quote; the rest is in Vi_sValueGap.     */
    /*------------------------------------------------------------------------*/
    static void Vrg_SplitTrailing(VoorheesItem item)
    {
        string sToken;                                   // the quoted string

        /* The string token at the start of the raw value.  A valid VRG_SZ   */
        /* always has one (an unclosed string is VRG_INVALID, not SZ), so    */
        /* the null test is a guard.  The Value box text, already decoded    */
        /* from the token alone, is not affected.                            */
        sToken = Vrg_QuotedToken(item.Vi_node.Vn_sRaw, 0);
        if (sToken != null && sToken.Length < item.Vi_node.Vn_sRaw.Length)
        {   /* Something after the closing quote: it becomes the item's    */
            /* trailing gap, so a new string written in its place keeps    */
            /* the line's trailing text, and the raw is the token alone.   */
            item.Vi_sValueGap = item.Vi_node.Vn_sRaw.Substring(sToken.Length);
            item.Vi_node.Vn_sRaw = sToken;
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_QuotedToken:                                                       */
    /*                                                                        */
    /* A double-quoted token at a position, through its closing quote (a      */
    /* backslash escapes the next character).                                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     s      : the text.                                                 */
    /*     iStart : where the opening quote is.                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the token, quotes included, or null when never closed.    */
    /*------------------------------------------------------------------------*/
    static string Vrg_QuotedToken(string s, int iStart)
    {
        int i;

        /* From the character after the opening quote to the first quote    */
        /* no backslash escapes.  Any backslash skips the character after   */
        /* it here, though Vrg_Unquote decodes only \\ and \" -- finding    */
        /* the end and decoding are different questions.                    */
        for (i = iStart + 1; i < s.Length; i++)
        {   /* One character inside the token per iteration. */
            if (s[i] == '\\')
            {   /* An escape: skip the next character, so an escaped quote   */
                /* does not end the token.                                   */
                i++;
            }
            else if (s[i] == '"')
            {   /* The closing quote: the token, both quotes included. */
                return(s.Substring(iStart, i + 1 - iStart));
            }
        }

        /* Never closed: the text ran out first (or ended in an escape). */
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_Unquote:                                                           */
    /*                                                                        */
    /* A quoted token's text: the quotes removed, "\\" and "\"" decoded (any  */
    /* other backslash kept as it is, as regedit does -- as found).  The      */
    /* reverse of Vrg_Quote for every text Vrg_Quote can make.                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sToken : the token, quotes included.                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the text.                                                 */
    /*------------------------------------------------------------------------*/
    static string Vrg_Unquote(string sToken)
    {
        StringBuilder sb;                                // the text
        int i;

        /* Every character between the two quotes, in order. */
        sb = new StringBuilder();
        for (i = 1; i < sToken.Length - 1; i++)
        {   /* One character of the token's inside per iteration. */
            if (sToken[i] == '\\' && i + 1 < sToken.Length - 1
                && (sToken[i + 1] == '\\' || sToken[i + 1] == '"'))
            {   /* An escape: the backslash dropped and the character after   */
                /* it taken.  The bound keeps the closing quote from being    */
                /* taken as an escaped one.                                   */
                i++;
            }
            sb.Append(sToken[i]);
        }

        /* The decoded text. */
        return(sb.ToString());
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_Quote:                                                             */
    /*                                                                        */
    /* Text as a quoted token: backslashes and quotes escaped -- the form     */
    /* regedit writes a string or a value name in (as found).                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sText : the text.                                                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : "\"" + escaped text + "\"".                               */
    /*------------------------------------------------------------------------*/
    static string Vrg_Quote(string sText)
    {
        /* Backslashes first, so the quotes' escapes are not doubled. */
        return("\"" + sText.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"");
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_QuoteName:                                                         */
    /*                                                                        */
    /* A value name as regedit writes it: "@" for the default (""), else      */
    /* quoted.                                                                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sName : the name.                                                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the token.                                                */
    /*------------------------------------------------------------------------*/
    static string Vrg_QuoteName(string sName)
    {
        if (sName.Length == 0)
        {   /* The default value: regedit writes it as a bare "@". */
            return("@");
        }

        /* Any other name: quoted with escapes.  Vrg_ParseLine compares a   */
        /* read name's token with this to decide whether it must be kept.   */
        return(Vrg_Quote(sName));
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_ParseValue:                                                        */
    /*                                                                        */
    /* A value's text after its "=" (its first line; hex continuation lines   */
    /* are added by Vrg_AddContinuation): its kind from its form, its raw     */
    /* text as written, its Value box text (Vrg_TextFromRaw).  A malformed    */
    /* value is a VRG_INVALID leaf whose text is the reason.  hex(2) and      */
    /* hex(7) strings are always read here as UTF-16: a REGEDIT4 file's are   */
    /* read again as ANSI only when the whole file is loaded                  */
    /* (Vrg_RereadStrings).                                                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sRaw : the value as written.                                       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNode : the value.                                          */
    /*------------------------------------------------------------------------*/
    static VoorheesNode Vrg_ParseValue(string sRaw)
    {
        VoorheesNode node;                               // the value
        string sError;                                   // why it is malformed
        string sText;                                    // its Value box text
        int iKind;                                       // its kind

        /* A malformed value keeps its raw text too: a wrapped hex value's   */
        /* first line is malformed until its continuation lines arrive       */
        /* (Vrg_AddContinuation reads it all again).                         */
        iKind = Vrg_KindOfRaw(sRaw);
        if (iKind == VRG_INVALID)
        {   /* Not a form regedit knows: a VRG_INVALID leaf whose text is   */
            /* the reason Vrg_LineProblem will give, the raw kept.          */
            node = VoorheesNode.Vn_NewLeaf(VRG_INVALID, "the value is not "
                + "\"text\", dword:, hex:, hex(N): or -");
            node.Vn_sRaw = sRaw;
            return(node);
        }

        /* Its Value box text, for a regedit 5 file (false: not ANSI); in   */
        /* a REGEDIT4 file the strings are read again as ANSI after the     */
        /* load (Vrg_RereadStrings) or the Raw edit (VRG_AfterRawEdit).     */
        sText = Vrg_TextFromRaw(iKind, sRaw, false, out sError);
        if (sText == null)
        {   /* Its form is known but its contents are not valid (yet): an   */
            /* invalid leaf with the specific reason, the raw kept for a    */
            /* continuation to complete.                                    */
            node = VoorheesNode.Vn_NewLeaf(VRG_INVALID, sError);
            node.Vn_sRaw = sRaw;
            return(node);
        }

        /* A valid value: its kind, its text, and its raw exactly as read,   */
        /* which is what gets written back while it is not edited.           */
        node = VoorheesNode.Vn_NewLeaf(iKind, sText);
        node.Vn_sRaw = sRaw;

        /* The value as written and as shown. */
        return(node);
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_KindOfRaw:                                                         */
    /*                                                                        */
    /* A value's kind from how it is written: "..." SZ, - DELETEVALUE,        */
    /* dword: DWORD, hex: BINARY, hex(2): EXPANDSZ, hex(7): MULTISZ, hex(b):  */
    /* QWORD, any other hex(N): HEXOTHER.                                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sRaw : the value as written.                                       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the VRG_ kind, or VRG_INVALID.                               */
    /*------------------------------------------------------------------------*/
    static int Vrg_KindOfRaw(string sRaw)
    {
        string sLower;                                   // the start, lower case
        string sType;                                    // a hex(N)'s N

        if (sRaw.StartsWith("\"", StringComparison.Ordinal))
        {   /* A string: it starts with a quote (whether it is closed is   */
            /* Vrg_TextFromRaw's question).                                */
            return(VRG_SZ);
        }

        if (sRaw.TrimEnd(' ', '\t') == "-")
        {   /* Delete the value: a lone "-", trailing spaces allowed. */
            return(VRG_DELETEVALUE);
        }

        /* The prefixes compare without case ("DWORD:" reads too), so the   */
        /* tests below work on a lower-case copy.                           */
        sLower = sRaw.ToLowerInvariant();
        if (sLower.StartsWith("dword:", StringComparison.Ordinal))
        {   /* 32 bits. */
            return(VRG_DWORD);
        }

        if (sLower.StartsWith("hex:", StringComparison.Ordinal))
        {   /* Bytes. */
            return(VRG_BINARY);
        }

        if (sLower.StartsWith("hex(", StringComparison.Ordinal) && sLower.IndexOf("):",
            StringComparison.Ordinal) > 4)
        {   /* hex(N): with something between the brackets: by its type   */
            /* number, compared as text as regedit writes it ("2", "7",   */
            /* "b"); any other spelling (a "02", say) is just HEXOTHER.   */
            sType = sLower.Substring(4, sLower.IndexOf("):", StringComparison.Ordinal) - 4);
            if (sType == "2")
            {   /* REG_EXPAND_SZ. */
                return(VRG_EXPANDSZ);
            }
            if (sType == "7")
            {   /* REG_MULTI_SZ. */
                return(VRG_MULTISZ);
            }
            if (sType == "b")
            {   /* REG_QWORD. */
                return(VRG_QWORD);
            }
            /* Any other type: kept as its bytes. */
            return(VRG_HEXOTHER);
        }

        /* Not a form regedit writes. */
        return(VRG_INVALID);
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_HexBytes:                                                          */
    /*                                                                        */
    /* The bytes of a hex value's text after its type prefix: pairs of hex    */
    /* digits separated by commas, with "\" line ends, line breaks and        */
    /* spaces between them ignored (as regedit reads them -- as found).  A    */
    /* single digit is taken as a byte; an empty piece (",," or a trailing    */
    /* comma) is refused.                                                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sHex   : the text after "hex:" or "hex(N):".                       */
    /*     sError : set to why it is not valid, else null.                    */
    /*                                                                        */
    /* Returns:                                                               */
    /*     byte[] : the bytes, or null when not valid.                        */
    /*------------------------------------------------------------------------*/
    static byte[] Vrg_HexBytes(string sHex, out string sError)
    {
        List<byte> lstBytes;                             // the bytes
        string[] arrParts;                               // the comma-separated pairs
        string sPart;                                    // one of them, cleaned
        int iValue;                                      // its value
        int i;

        /* No objection yet, and no bytes.  The wrapping (every "\" and line   */
        /* break) and all spaces and tabs are stripped first -- anywhere,      */
        /* not only between pairs, so "a b" reads as "ab" -- and what is       */
        /* left is split at the commas.                                        */
        sError = null;
        lstBytes = new List<byte>();
        arrParts = sHex.Replace("\\", "").Replace("\r", "").Replace("\n", "")
            .Replace(" ", "").Replace("\t", "").Split(',');
        for (i = 0; i < arrParts.Length; i++)
        {   /* One piece between commas per iteration. */
            sPart = arrParts[i];
            if (sPart.Length == 0 && arrParts.Length == 1)
            {   /* Nothing at all ("hex:" with no data): no bytes, which is   */
                /* valid -- an empty value.                                   */
                break;
            }
            if (sPart.Length == 0 || sPart.Length > 2 || !int.TryParse(sPart,
                NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture,
                out iValue))
            {   /* Not a hex byte (empty, too long, or not hex digits):   */
                /* refused, the piece quoted so the user can find it.     */
                sError = "\"" + sPart + "\" is not a hex byte (two hex digits "
                    + "between commas)";
                return(null);
            }
            lstBytes.Add((byte)iValue);
        }

        /* Every byte, in order. */
        return(lstBytes.ToArray());
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_PairsText:                                                         */
    /*                                                                        */
    /* Bytes as the Value box shows them: lower-case hex pairs and commas --  */
    /* the same form regedit writes them in, without its wrapping.            */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     bytes : the bytes.                                                 */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : e.g. "0a,ff,00" ("" for none).                            */
    /*------------------------------------------------------------------------*/
    static string Vrg_PairsText(byte[] bytes)
    {
        StringBuilder sb;                                // the text
        int i;

        /* Each byte as two lower-case digits, a comma between them. */
        sb = new StringBuilder();
        for (i = 0; i < bytes.Length; i++)
        {   /* One byte per iteration. */
            if (i > 0)
            {   /* Between pairs. */
                sb.Append(',');
            }
            sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
        }

        /* The pairs. */
        return(sb.ToString());
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_StringEncoding:                                                    */
    /*                                                                        */
    /* How hex(2) and hex(7) strings are stored: UTF-16 LE, or the system's   */
    /* ANSI code page in a REGEDIT4 file.                                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     bAnsi : a REGEDIT4 file.                                           */
    /*                                                                        */
    /* Returns:                                                               */
    /*     Encoding : the encoding.                                           */
    /*------------------------------------------------------------------------*/
    static Encoding Vrg_StringEncoding(bool bAnsi)
    {
        if (bAnsi)
        {   /* REGEDIT4: the system's ANSI code page, its ENCODER set to    */
            /* throw on a character the page cannot hold (the default one   */
            /* silently writes "?" or a best-fit look-alike).  An edit is   */
            /* refused before that (VRG_AnsiProblem); this is the save's    */
            /* own guard, and the editor's save reports the exception.      */
            /* Decoding keeps the page's own fallback, so reading is as     */
            /* before.                                                      */
            return(Encoding.GetEncoding(Encoding.Default.CodePage, EncoderFallback.ExceptionFallback,
                Encoding.Default.DecoderFallback));
        }

        /* regedit 5: UTF-16 little-endian (false), and no byte order mark   */
        /* (false) -- the bytes go inside a value, not at a file's start.    */
        return(new UnicodeEncoding(false, false));
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_TextFromRaw:                                                       */
    /*                                                                        */
    /* A value's Value box text from how it is written:                       */
    /*     SZ        the string, decoded;                                     */
    /*     DWORD     "0x" + 8 hex digits (fewer digits accepted on read);     */
    /*     QWORD     "0x" + 16 hex digits, from up to 8 little-endian bytes;  */
    /*     BINARY    hex pairs "aa,bb";                                       */
    /*     EXPANDSZ  the string, from UTF-16 (ANSI for REGEDIT4) less its     */
    /*               final NUL;                                               */
    /*     MULTISZ   one string per line (the final empty strings dropped);   */
    /*     HEXOTHER  "hex(N):" + its pairs;                                   */
    /*     DELETE    "".                                                      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iKind  : the value's kind.                                         */
    /*     sRaw   : the value as written.                                     */
    /*     bAnsi  : a REGEDIT4 file.                                          */
    /*     sError : set to why it is not valid, else null.                    */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the text, or null when not valid.                         */
    /*------------------------------------------------------------------------*/
    static string Vrg_TextFromRaw(int iKind, string sRaw, bool bAnsi,
        out string sError)
    {
        string sToken;                                   // a string's token
        string sDigits;                                  // a dword's digits
        string sText;                                    // decoded string data
        byte[] bytes;                                    // a hex value's bytes
        uint uValue;                                     // a dword
        ulong ulValue;                                   // a qword
        int iColon;                                      // the end of a hex prefix
        int i;

        /* No objection yet.  The three forms that are not hex are done at   */
        /* once; every hex form falls through to read its bytes.             */
        sError = null;
        switch (iKind)
        {   /* By kind: the non-hex forms. */
            case VRG_SZ:
                /* A quoted string, closed: its text decoded.  An unclosed   */
                /* one is refused with its reason.                           */
                sToken = Vrg_QuotedToken(sRaw, 0);
                if (sToken == null)
                {   /* Never closed. */
                    sError = "the string's closing quote is missing";
                    return(null);
                }
                return(Vrg_Unquote(sToken));

            case VRG_DELETEVALUE:
                /* Nothing to show: the value only says "delete". */
                return("");

            case VRG_DWORD:
                /* 1 to 8 hex digits after "dword:" (6 characters, matched   */
                /* without case by Vrg_KindOfRaw), spaces round them         */
                /* allowed; shown always as 0x and eight digits.             */
                sDigits = sRaw.Substring(6).Trim(' ', '\t');
                if (sDigits.Length == 0 || sDigits.Length > 8 || !uint.TryParse(sDigits,
                    NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture,
                    out uValue))
                {   /* Not a dword: refused with what it needs. */
                    sError = "dword: needs 1 to 8 hex digits";
                    return(null);
                }
                return("0x" + uValue.ToString("x8", CultureInfo.InvariantCulture));

            default:
                /* A hex form: its bytes first. */
                break;
        }

        /* The bytes after the type prefix's colon (every hex form has   */
        /* one: Vrg_KindOfRaw matched "hex:" or "hex(N):").              */
        iColon = sRaw.IndexOf(':');
        bytes = Vrg_HexBytes(sRaw.Substring(iColon + 1), out sError);
        if (bytes == null)
        {   /* Not valid hex: refused, sError naming the bad piece. */
            return(null);
        }

        switch (iKind)
        {   /* By kind: what the bytes mean. */
            case VRG_QWORD:
                /* Up to 8 little-endian bytes (fewer read as the low ones). */
                if (bytes.Length > 8)
                {   /* Too many for 64 bits: refused. */
                    sError = "hex(b): holds at most 8 bytes";
                    return(null);
                }
                ulValue = 0;
                for (i = bytes.Length - 1; i >= 0; i--)
                {   /* One byte per iteration, most significant (the last)   */
                    /* first, each shifting the earlier ones up.             */
                    ulValue = (ulValue << 8) | bytes[i];
                }
                return("0x" + ulValue.ToString("x16", CultureInfo.InvariantCulture));

            case VRG_EXPANDSZ:
                /* One string, less its final NUL. */
                sText = Vrg_StringEncoding(bAnsi).GetString(bytes);
                if (sText.EndsWith("\0", StringComparison.Ordinal))
                {   /* The terminator: dropped (Vrg_BytesOfText adds it   */
                    /* back when the value is written afresh).            */
                    sText = sText.Substring(0, sText.Length - 1);
                }
                return(sText);

            case VRG_MULTISZ:
                /* Strings separated by NULs, the list ended by an empty one. */
                sText = Vrg_StringEncoding(bAnsi).GetString(bytes);
                while (sText.EndsWith("\0", StringComparison.Ordinal))
                {   /* One trailing NUL per iteration: the terminators, and   */
                    /* any empty strings at the list's end with them.         */
                    sText = sText.Substring(0, sText.Length - 1);
                }
                /* Each NUL left separates two strings: one per line. */
                return(sText.Replace('\0', '\n'));

            case VRG_HEXOTHER:
                /* Its type kept, its bytes as pairs. */
                return(sRaw.Substring(0, iColon + 1).ToLowerInvariant()
                    + Vrg_PairsText(bytes));

            default:
                /* BINARY: the pairs. */
                return(Vrg_PairsText(bytes));
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_ContinuesAfter:                                                    */
    /*                                                                        */
    /* Whether a value's line goes on to the next one: a hex value whose line */
    /* ends with "\" (regedit's wrapping).  Strings never wrap.  Asked by the */
    /* line engine (VoorheesLines.cs, Vln_TakeContinuations) after each line  */
    /* of a value, with the item as parsed so far.                            */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     item  : the value item.                                            */
    /*     sLine : the line so far (its last line).                           */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when the next line belongs to it.                      */
    /*------------------------------------------------------------------------*/
    public static bool Vrg_ContinuesAfter(VoorheesItem item, string sLine)
    {
        int iKind;                                       // the value's kind (from its raw form)

        if (item.Vi_node.Vn_sRaw == null)
        {   /* No raw text: not a value read from the file (a line that is   */
            /* not a value at all is made with none), so nothing runs on.    */
            return(false);
        }

        /* The kind as parsed.  A wrapped hex value's first line alone is    */
        /* malformed -- its "\" leaves a trailing comma -- so an invalid     */
        /* value with a name and "=" is judged as binary here, by its line   */
        /* (the whole value is parsed again once its lines are gathered).    */
        iKind = item.Vi_node.Vn_iKind;
        if (iKind == VRG_INVALID && item.Vi_sKeyGap != null)
        {   /* Possibly a hex value whose bytes run on: judge by the line itself. */
            iKind = VRG_BINARY;
        }

        /* A hex form (not a string, a dword or a delete), on a value line   */
        /* (it has a key gap), whose line ends in a backslash, trailing      */
        /* spaces aside.                                                     */
        return(iKind != VRG_SZ && iKind != VRG_DWORD && iKind != VRG_DELETEVALUE
            && item.Vi_sKeyGap != null && sLine.TrimEnd(' ', '\t').EndsWith("\\",
            StringComparison.Ordinal));
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_AddContinuation:                                                   */
    /*                                                                        */
    /* Adds a hex value's continuation lines to it: the raw value takes them  */
    /* exactly as written, and the Value box text is worked out again from    */
    /* all of it.  Called by the line engine (VoorheesLines.cs,               */
    /* Vln_AddContinuation) once a value's last line is known.                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     item  : the value.                                                 */
    /*     sMore : the continuation text (it starts with a line break).       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the value is replaced by its whole form.                    */
    /*------------------------------------------------------------------------*/
    public static void Vrg_AddContinuation(VoorheesItem item, string sMore)
    {
        string sFirst;                                   // the first line's value as written

        /* The first line's value as written (its raw text). */
        sFirst = item.Vi_node.Vn_sRaw;
        if (sFirst == null)
        {   /* Never: a parsed value always keeps its raw text, and only a   */
            /* value with raw text is continued (Vrg_ContinuesAfter).  A     */
            /* guard.                                                        */
            sFirst = "";
        }

        /* The whole value, read again from all its lines -- valid now, or   */
        /* still not, which Vrg_LineProblem then reports.  The raw keeps     */
        /* the lines exactly, breaks and indentation included; the hex       */
        /* reader ignores them.                                              */
        item.Vi_node = Vrg_ParseValue(sFirst + sMore);
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_LineProblem:                                                       */
    /*                                                                        */
    /* Why a line read from a file (or the Raw box) is refused, if it is:     */
    /* a line that is not a key, a value or a comment; a malformed value; a   */
    /* value outside any key; a value under a key being deleted.              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     item  : the item the line made.                                    */
    /*     under : the key's container it falls under, or null before the     */
    /*             first key.                                                 */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the reason (to follow "Line L, column C: "), or null.     */
    /*------------------------------------------------------------------------*/
    public static string Vrg_LineProblem(VoorheesItem item, VoorheesNode under)
    {
        if (item.Vi_iKind == VoorheesItem.VI_COMMENT || item.Vi_node.Vn_IsContainer())
        {   /* A comment or a key: fine anywhere (a key's path is not        */
            /* checked here -- a key not under a hive is only warned about   */
            /* when the file opens, Vrg_OpenWarnings).                       */
            return(null);
        }

        if (item.Vi_sKeyGap == null)
        {   /* No name and "=": Vrg_ParseLine's mark for a line that is   */
            /* none of the three.                                         */
            return("this line is not a [key], a value (\"name\"=... or @=...) "
                + "or a ; comment");
        }

        if (item.Vi_node.Vn_iKind == VRG_INVALID)
        {   /* A malformed value: its reason, which Vrg_ParseValue stored   */
            /* as the leaf's text.                                          */
            return(item.Vi_node.Vn_sText);
        }

        if (under == null)
        {   /* Before the first key (or a Raw edit at the top level): a   */
            /* value has no key to belong to.                             */
            return("a value must be inside a key: put a [HKEY_...] line above it");
        }

        if (under.Vn_iKind == VRG_DELETEKEY)
        {   /* Under [-key]: the key is deleted, so a value there means   */
            /* nothing.                                                   */
            return("a key being deleted ([-...]) holds no values");
        }

        /* A valid value, in a key. */
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_WriteEntry:                                                        */
    /*                                                                        */
    /* A value's line(s): its name ("@", or quoted: as written when it was),  */
    /* the "=" (with its spaces as written), the value (as written, or        */
    /* generated in regedit's form: Vrg_GenerateValue), trailing spaces.      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc   : the document (line ending, REGEDIT4).                      */
    /*     item  : the value.                                                 */
    /*     sLead : the lead written before it (its indentation counts toward  */
    /*             a hex value's wrap column).                                */
    /*     sb    : where the text goes.                                       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the text is appended.                                       */
    /*------------------------------------------------------------------------*/
    public static void Vrg_WriteEntry(VoorheesDocument doc, VoorheesItem item,
        string sLead, StringBuilder sb)
    {
        string sName;                                    // the name as written
        string sGap;                                     // name to value

        if (item.Vi_sKeyRaw != null)
        {   /* As written: the token Vrg_ParseLine kept ("@", or a quoting   */
            /* generation would not give back).  A rename clears it.         */
            sName = item.Vi_sKeyRaw;
        }
        else
        {   /* Generated from the name: "@" or quoted with escapes. */
            sName = Vrg_QuoteName(item.Vi_sKey);
        }

        if (item.Vi_sKeyGap != null)
        {   /* As written: any spaces round the "=" kept. */
            sGap = item.Vi_sKeyGap;
        }
        else
        {   /* regedit's: a bare "=". */
            sGap = "=";
        }

        /* Name and gap, then the value. */
        sb.Append(sName);
        sb.Append(sGap);
        if (item.Vi_node.Vn_sRaw != null)
        {   /* As written, continuation lines and all. */
            sb.Append(item.Vi_node.Vn_sRaw);
        }
        else
        {   /* A new or edited value: generated in regedit's form, a hex   */
            /* value wrapped counting from the column the value starts     */
            /* at -- the line's indentation, name and gap before it.       */
            sb.Append(Vrg_GenerateValue(doc, item.Vi_node, Vrg_OwnIndent(sLead).Length
                + sName.Length + sGap.Length));
        }

        if (item.Vi_sValueGap != null)
        {   /* Trailing text as written (after a string's closing quote). */
            sb.Append(item.Vi_sValueGap);
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_OwnIndent:                                                         */
    /*                                                                        */
    /* An item's own indentation: what follows the last line break in its     */
    /* lead.                                                                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sLead : the lead, or null.                                         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the indentation ("" for none).                            */
    /*------------------------------------------------------------------------*/
    static string Vrg_OwnIndent(string sLead)
    {
        if (sLead == null)
        {   /* No lead known (a new item): no indentation. */
            return("");
        }

        /* After the last break (or the whole lead when it has none: the   */
        /* file's first line, where -1 + 1 is 0).                          */
        return(sLead.Substring(sLead.LastIndexOfAny(new char[] { '\r', '\n' }) + 1));
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_GenerateValue:                                                     */
    /*                                                                        */
    /* A value in regedit's own form, from its text: SZ quoted with escapes;  */
    /* DWORD "dword:" + 8 hex digits; the hex forms "hex:" / "hex(N):" + the  */
    /* bytes as comma-separated pairs, wrapped as regedit wraps them (after a */
    /* byte and its comma, once the line is longer than 76 characters: ",\",  */
    /* a line break, two spaces); DELETEVALUE "-".                            */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc     : the document (line ending, REGEDIT4 strings).            */
    /*     node    : the value (its text valid for its kind).                 */
    /*     iColumn : the column the value starts at (the line's text before   */
    /*               it).                                                     */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the value as written.                                     */
    /*------------------------------------------------------------------------*/
    static string Vrg_GenerateValue(VoorheesDocument doc, VoorheesNode node,
        int iColumn)
    {
        StringBuilder sb;                                // the value
        string sPrefix;                                  // a hex form's type prefix
        byte[] bytes;                                    // its bytes
        int iLineLength;                                 // the current line's length so far
        int i;

        if (node.Vn_iKind == VRG_SZ)
        {   /* A string: quoted with escapes, on one line (strings never   */
            /* wrap, and Vrg_ValueProblem keeps line breaks out).          */
            return(Vrg_Quote(node.Vn_sText));
        }

        if (node.Vn_iKind == VRG_DELETEVALUE)
        {   /* Delete it: a bare "-". */
            return("-");
        }

        if (node.Vn_iKind == VRG_DWORD)
        {   /* "dword:" and eight lower-case hex digits, as regedit writes   */
            /* one; the text (0x... or decimal) is read as a number first.   */
            return("dword:" + Vrg_ParseNumber(node.Vn_sText, false).ToString("x8",
                CultureInfo.InvariantCulture));
        }

        /* A hex form: its bytes and type prefix from its text -- strings    */
        /* in the file's own encoding (ANSI for REGEDIT4) -- then the        */
        /* prefix, counted into the line's length from the value's column.   */
        bytes = Vrg_BytesOfText(node.Vn_iKind, node.Vn_sText,
            Vrg_IsAnsi(doc.Vd_root), out sPrefix);
        sb = new StringBuilder();
        sb.Append(sPrefix);
        iLineLength = iColumn + sPrefix.Length;
        for (i = 0; i < bytes.Length; i++)
        {   /* One byte per iteration: its two digits, then -- unless it is   */
            /* the last -- its comma and, past the wrap column, the break.    */
            sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
            iLineLength += 2;
            if (i == bytes.Length - 1)
            {   /* The last byte: no comma, and so never a wrap after it. */
                break;
            }
            sb.Append(',');
            iLineLength++;
            if (iLineLength > VRG_WRAPAFTER)
            {   /* Past the wrap column with more to come: "\", the          */
                /* document's line ending, and the two-space indent the      */
                /* next line starts its count from.  The check comes after   */
                /* the comma, so a line ends ",\" as regedit's do.           */
                sb.Append('\\');
                sb.Append(doc.Vd_sNewline);
                sb.Append(VRG_WRAPINDENT);
                iLineLength = VRG_WRAPINDENT.Length;
            }
        }

        /* The hex value, wrapped ("hex:" alone for no bytes). */
        return(sb.ToString());
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_ParseNumber:                                                       */
    /*                                                                        */
    /* A DWORD's or QWORD's text as a number: "0x" + hex digits, or decimal.  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sText  : the text (already checked by Vrg_NumberProblem).          */
    /*     bQword : 64 bits rather than 32.                                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     ulong : the number (0 when it does not read).                      */
    /*------------------------------------------------------------------------*/
    static ulong Vrg_ParseNumber(string sText, bool bQword)
    {
        ulong ulValue;                                   // the number
        string sTrim;                                    // the text trimmed

        /* The same two forms Vrg_NumberProblem accepts, white space at the   */
        /* ends ignored: "0x" (either case) and hex digits, or plain          */
        /* decimal digits.  Every caller passes text already checked, or      */
        /* stored in the 0x form, so the zero fallbacks below are guards.     */
        sTrim = sText.Trim();
        if (sTrim.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {   /* Hex: the digits after "0x". */
            if (!ulong.TryParse(sTrim.Substring(2), NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out ulValue))
            {   /* Not hex (refused before this is reached). */
                ulValue = 0;
            }
        }
        else if (!ulong.TryParse(sTrim, NumberStyles.None, CultureInfo.InvariantCulture,
            out ulValue))
        {   /* Not decimal either (no sign or separators are taken). */
            ulValue = 0;
        }

        if (!bQword && ulValue > uint.MaxValue)
        {   /* Too big for a DWORD (refused before this is reached). */
            ulValue = 0;
        }

        /* The number. */
        return(ulValue);
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_NumberProblem:                                                     */
    /*                                                                        */
    /* Why text is not a DWORD or QWORD, if it is not: "0x" + 1 to 8 (16) hex */
    /* digits, or a decimal number that fits.                                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sText  : the text.                                                 */
    /*     bQword : 64 bits rather than 32.                                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the reason, or null.                                      */
    /*------------------------------------------------------------------------*/
    static string Vrg_NumberProblem(string sText, bool bQword)
    {
        ulong ulValue;                                   // the number
        string sTrim;                                    // the text trimmed
        string sWhat;                                    // DWORD or QWORD
        int iMaxDigits;                                  // 8 or 16

        /* White space at the ends ignored; the size's name and digit limit   */
        /* set for the messages, a DWORD's unless it is a QWORD.              */
        sTrim = sText.Trim();
        sWhat = "A DWORD";
        iMaxDigits = 8;
        if (bQword)
        {   /* 64 bits: 16 hex digits. */
            sWhat = "A QWORD";
            iMaxDigits = 16;
        }

        if (sTrim.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {   /* Hex: at least one digit after "0x", no more than the size   */
            /* holds -- counted in digits, so leading zeros count too.     */
            if (sTrim.Length < 3 || sTrim.Length - 2 > iMaxDigits || !ulong.TryParse(
                sTrim.Substring(2), NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out ulValue))
            {   /* Not 1 to N hex digits. */
                return(sWhat + " is 0x and 1 to " + iMaxDigits.ToString(
                    CultureInfo.InvariantCulture) + " hex digits, or a decimal number.");
            }
            return(null);
        }

        if (!ulong.TryParse(sTrim, NumberStyles.None, CultureInfo.InvariantCulture,
            out ulValue) || (!bQword && ulValue > uint.MaxValue))
        {   /* Not a decimal that fits: the message gives the range. */
            return(sWhat + " is 0x and hex digits, or a decimal number from 0 to "
                + (bQword ? "18446744073709551615" : "4294967295") + ".");
        }

        /* A number of the size. */
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_BytesOfText:                                                       */
    /*                                                                        */
    /* A hex value's bytes and type prefix, from its Value box text (already  */
    /* valid: Vrg_ValueProblem): QWORD 8 little-endian bytes; EXPANDSZ the    */
    /* string and a NUL; MULTISZ each line and a NUL, then a NUL; BINARY the  */
    /* pairs; HEXOTHER its "hex(N):" and pairs.                               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iKind   : the kind.                                                */
    /*     sText   : the text.                                                */
    /*     bAnsi   : a REGEDIT4 file (strings as ANSI bytes).                 */
    /*     sPrefix : set to "hex:" or "hex(N):".                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     byte[] : the bytes.                                                */
    /*------------------------------------------------------------------------*/
    static byte[] Vrg_BytesOfText(int iKind, string sText, bool bAnsi,
        out string sPrefix)
    {
        byte[] bytes;                                    // the bytes
        string sError;                                   // (valid text: unused)
        string sLines;                                   // MULTISZ lines with NULs
        ulong ulValue;                                   // a qword
        int iColon;                                      // a HEXOTHER prefix's end
        int i;

        switch (iKind)
        {   /* By kind: the type prefix, and the bytes the text stands for. */
            case VRG_QWORD:
                /* 8 bytes, least significant first -- always all eight,   */
                /* though a read value may have had fewer.                 */
                sPrefix = "hex(b):";
                ulValue = Vrg_ParseNumber(sText, true);
                bytes = new byte[8];
                for (i = 0; i < 8; i++)
                {   /* Byte i: the number shifted down 8 * i bits, cut to 8. */
                    bytes[i] = (byte)(ulValue >> (8 * i));
                }
                return(bytes);

            case VRG_EXPANDSZ:
                /* The string and its NUL, in the file's string encoding.   */
                /* (A CRLF is folded to LF first, though Vrg_ValueProblem   */
                /* already keeps line breaks out of this kind.)             */
                sPrefix = "hex(2):";
                return(Vrg_StringEncoding(bAnsi).GetBytes(sText.Replace("\r\n", "\n")
                    + "\0"));

            case VRG_MULTISZ:
                /* Each line and a NUL, then the list's NUL. */
                sPrefix = "hex(7):";
                sLines = sText.Replace("\r\n", "\n").Replace('\r', '\n');
                if (sLines.Length == 0)
                {   /* An empty list: just the terminator. */
                    return(Vrg_StringEncoding(bAnsi).GetBytes("\0"));
                }
                /* Every line break becomes the NUL ending its string, and   */
                /* two more NULs end the last string and the list.           */
                return(Vrg_StringEncoding(bAnsi).GetBytes(sLines.Replace('\n', '\0')
                    + "\0\0"));

            case VRG_HEXOTHER:
                /* Its own type, its pairs: the text is "hex(N):" and the      */
                /* pairs (Vrg_ValueProblem checked that), the prefix written   */
                /* in lower case.                                              */
                iColon = sText.IndexOf(':');
                sPrefix = sText.Substring(0, iColon + 1).ToLowerInvariant();
                return(Vrg_HexBytes(sText.Substring(iColon + 1), out sError));

            default:
                /* BINARY: the pairs (commas or spaces between them). */
                sPrefix = "hex:";
                return(Vrg_HexBytes(Vrg_PairsInput(sText), out sError));
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_PairsInput:                                                        */
    /*                                                                        */
    /* Typed binary as comma-separated pairs: spaces, tabs and line breaks    */
    /* between pairs are taken as commas.                                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sText : the typed text.                                            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the pairs with commas.                                    */
    /*------------------------------------------------------------------------*/
    static string Vrg_PairsInput(string sText)
    {
        string[] arrParts;                               // the pieces between separators
        List<string> lstPairs;                           // the non-empty ones

        /* Split at every separator a user might type between pairs; runs     */
        /* of them ("aa, bb") leave empty pieces, which are dropped -- so     */
        /* unlike Vrg_HexBytes on a file's text, ",," is not an error here.   */
        arrParts = sText.Split(new char[] { ',', ' ', '\t', '\r', '\n' });
        lstPairs = new List<string>();
        foreach (string sPart in arrParts)
        {   /* One piece per iteration. */
            if (sPart.Length > 0)
            {   /* A pair (whether it is valid hex is Vrg_HexBytes' to say). */
                lstPairs.Add(sPart);
            }
        }

        /* Comma-separated, the form Vrg_HexBytes reads. */
        return(string.Join(",", lstPairs.ToArray()));
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_ValueProblem:                                                      */
    /*                                                                        */
    /* Why text cannot be a value of a kind, if it cannot: SZ one line        */
    /* (regedit keeps line breaks only as hex(1)); DWORD / QWORD numbers;     */
    /* BINARY hex pairs; HEXOTHER "hex(N):" and pairs; EXPANDSZ one line;     */
    /* MULTISZ any lines.  Used by Vrg_MakeValue and by the dispatcher's      */
    /* import of a value from another format (VoorheesFormat.cs).  The file's */
    /* kind is not known here: a REGEDIT4 file's code-page rule is            */
    /* VRG_AnsiProblem's, applied by the dispatcher, which has the document.  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iKind : the kind.                                                  */
    /*     sText : the text.                                                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the reason, or null.                                      */
    /*------------------------------------------------------------------------*/
    public static string Vrg_ValueProblem(int iKind, string sText)
    {
        string sError;                                   // a hex problem
        int iColon;                                      // a HEXOTHER prefix's end

        switch (iKind)
        {   /* By kind: the one rule each has. */
            case VRG_SZ:
            case VRG_EXPANDSZ:
                /* One line: a string is written on its value's own line   */
                /* (an expandable one as hex, but kept one line all the    */
                /* same, as the message says).                             */
                if (sText.IndexOf('\r') >= 0 || sText.IndexOf('\n') >= 0)
                {   /* Several lines: refused, pointing to the kind that   */
                    /* holds a list.                                       */
                    return("A registry string is one line (regedit stores line "
                        + "breaks only in hex form); use a Multi-String for a list.");
                }
                return(null);

            case VRG_DWORD:
                /* 32 bits. */
                return(Vrg_NumberProblem(sText, false));

            case VRG_QWORD:
                /* 64 bits. */
                return(Vrg_NumberProblem(sText, true));

            case VRG_BINARY:
                /* Hex pairs, read as typed text (Vrg_PairsInput lets spaces   */
                /* stand for commas) by the same reader as a file's bytes.     */
                if (Vrg_HexBytes(Vrg_PairsInput(sText), out sError) == null)
                {   /* Not pairs: refused, with the bad piece named. */
                    return("Binary data is hex pairs separated by commas or "
                        + "spaces: " + sError + ".");
                }
                return(null);

            case VRG_HEXOTHER:
                /* hex(N): and pairs: the type is part of the text, so it   */
                /* must start "hex(", and a ")" must sit just before the    */
                /* first colon, with at least one character between them.   */
                iColon = sText.IndexOf(':');
                if (!sText.StartsWith("hex(", StringComparison.OrdinalIgnoreCase)
                    || iColon < 6 || sText[iColon - 1] != ')')
                {   /* Not hex(N):. */
                    return("This value is written hex(N): followed by hex pairs.");
                }
                if (Vrg_HexBytes(sText.Substring(iColon + 1), out sError) == null)
                {   /* Not pairs (here strictly comma-separated, as in a   */
                    /* file): refused, with the bad piece named.           */
                    return("This value is written hex(N): followed by hex pairs: "
                        + sError + ".");
                }
                return(null);

            default:
                /* MULTISZ (any lines, one string each) and DELETEVALUE (no   */
                /* text at all): anything.                                    */
                return(null);
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_MakeValue:                                                         */
    /*                                                                        */
    /* A value of a kind from text (the Value box, in-place editing,          */
    /* Replace), its text in the Value box's own form (numbers as 0x...,      */
    /* pairs lower case and comma-separated), or why it can't be one.  Also   */
    /* used by the dispatcher's import from another format                    */
    /* (VoorheesFormat.cs).                                                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iKind  : the kind.                                                 */
    /*     sText  : the text.                                                 */
    /*     sError : set to why it is refused, else null.                      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNode : the value (to be written in regedit's form), or     */
    /*                    null when refused.                                  */
    /*------------------------------------------------------------------------*/
    public static VoorheesNode Vrg_MakeValue(int iKind, string sText,
        out string sError)
    {
        string sValue;                                   // the text in the Value box's form
        string sPrefix;                                  // (unused)
        byte[] bytes;                                    // binary bytes

        /* The kind's one rule first; a refusal carries its reason out. */
        sError = Vrg_ValueProblem(iKind, sText);
        if (sError != null)
        {   /* Refused: no value, the reason in sError. */
            return(null);
        }

        /* The text in the Value box's own form: line breaks made LF (a      */
        /* MULTI_SZ's lines), and below, numbers and pairs written the one   */
        /* way the box shows them, so an edit that changes nothing is seen   */
        /* as unchanged (Vrg_ValueFromPanel's comparison) next time.         */
        sValue = sText.Replace("\r\n", "\n").Replace('\r', '\n');
        if (iKind == VRG_DWORD)
        {   /* As 0x and eight digits (decimal typed in is converted). */
            sValue = "0x" + Vrg_ParseNumber(sText, false).ToString("x8",
                CultureInfo.InvariantCulture);
        }
        else if (iKind == VRG_QWORD)
        {   /* As 0x and sixteen digits. */
            sValue = "0x" + Vrg_ParseNumber(sText, true).ToString("x16",
                CultureInfo.InvariantCulture);
        }
        else if (iKind == VRG_BINARY)
        {   /* As lower-case pairs with commas, from the bytes the typed   */
            /* text stands for (false: no strings here, so the encoding    */
            /* does not matter).                                           */
            bytes = Vrg_BytesOfText(iKind, sText, false, out sPrefix);
            sValue = Vrg_PairsText(bytes);
        }
        else if (iKind == VRG_DELETEVALUE)
        {   /* Nothing: a value to delete has no text. */
            sValue = "";
        }

        /* A new value with no raw text, so Vrg_WriteEntry generates it in   */
        /* regedit's form.                                                   */
        return(VoorheesNode.Vn_NewLeaf(iKind, sValue));
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_LoadDocument:                                                      */
    /*                                                                        */
    /* A whole file's bytes to a registry document: decoded, read by the line */
    /* engine (the header first; refused with line and column where regedit   */
    /* would not take it), its hex(2) / hex(7) strings read again as ANSI for */
    /* a REGEDIT4 file.                                                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     bytes  : the file's contents.                                      */
    /*     sPath  : the file, for the document to remember; may be null.      */
    /*     sError : set to why the file is refused, else null.                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesDocument : the document, or null when refused.             */
    /*------------------------------------------------------------------------*/
    public static VoorheesDocument Vrg_LoadDocument(byte[] bytes, string sPath,
        out string sError)
    {
        Encoding encoding;                               // the file's encoding
        bool bBom;                                       // it had a byte order mark
        string sText;                                    // the decoded file
        string sNewline;                                 // its line ending
        VoorheesNode root;                               // the document node

        /* The bytes as text (a regedit 5 export is UTF-16 with a mark; the    */
        /* mark is noted so the save keeps it, and a marked file whose bytes   */
        /* do not decode is refused -- VoorheesText.cs), then every line       */
        /* read by the line engine, the header checked first.                  */
        sText = VoorheesText.Vtx_Decode(bytes, out encoding, out bBom, out sError);
        if (sText == null)
        {   /* Refused by the decoder: sError says why. */
            return(null);
        }

        root = VoorheesLines.Vln_Parse(VoorheesFormat.VF_REG, sText, null,
            out sError);
        if (root == null)
        {   /* Refused where regedit would refuse it: no document, sError   */
            /* giving line, column and reason.                              */
            return(null);
        }

        if (Vrg_IsAnsi(root))
        {   /* REGEDIT4: its strings in hex are ANSI, but the parse read     */
            /* them as UTF-16 (it does not know the header) -- read again.   */
            Vrg_RereadStrings(root);
        }

        /* The line ending for generated text: the file's own, or CRLF        */
        /* (regedit's) for a file with no line break.  (A file whose breaks   */
        /* are lone CRs gets null from Vtx_DetectNewline, which Vd_Create     */
        /* turns into its default, CRLF.)                                     */
        sNewline = "\r\n";
        if (sText.IndexOf('\r') >= 0 || sText.IndexOf('\n') >= 0)
        {   /* Line breaks to go by. */
            sNewline = VoorheesText.Vtx_DetectNewline(sText);
        }

        /* The document; its delimiter and comment marker are regedit's,    */
        /* "=" and ";", from Vd_Create's defaults (Vf_ApplyDefaultStyle).   */
        return(VoorheesDocument.Vd_Create(VoorheesFormat.VF_REG, root, sPath,
            encoding, bBom, sNewline, null, null, null));
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_RereadStrings:                                                     */
    /*                                                                        */
    /* In a REGEDIT4 file, the Value box text of every hex(2) and hex(7)      */
    /* value read again with ANSI bytes.                                      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     container : the document node (or a key).                          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the values' texts are replaced.                             */
    /*------------------------------------------------------------------------*/
    static void Vrg_RereadStrings(VoorheesNode container)
    {
        VoorheesNode node;                               // a value
        int i;

        for (i = 0; i < container.Vn_Count(); i++)
        {   /* One item per iteration: a comment, a key or a value. */
            node = container.Vn_lstItems[i].Vi_node;
            if (node == null)
            {   /* A comment: no value node. */
                continue;
            }

            if (node.Vn_IsContainer())
            {   /* A key: its values, the same way (keys do not nest, so   */
                /* the recursion is one level deep).                       */
                Vrg_RereadStrings(node);
            }
            else
            {   /* A value: re-read if it is a string in hex. */
                Vrg_RereadValue(node);
            }
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_RereadValue:                                                       */
    /*                                                                        */
    /* One value's Value box text read again with ANSI bytes, when it is an   */
    /* hex(2) or hex(7) string (the parse read it as UTF-16); any other value */
    /* is left alone.                                                         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     node : the value.                                                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : its text replaced when it is such a string.                 */
    /*------------------------------------------------------------------------*/
    static void Vrg_RereadValue(VoorheesNode node)
    {
        string sError;                                   // (valid already: unused)
        string sText;                                    // its text read as ANSI

        if ((node.Vn_iKind == VRG_EXPANDSZ || node.Vn_iKind == VRG_MULTISZ)
            && node.Vn_sRaw != null)
        {   /* A string in hex, as read: its text again from the same raw   */
            /* bytes, decoded as ANSI.                                      */
            sText = Vrg_TextFromRaw(node.Vn_iKind, node.Vn_sRaw, true, out sError);
            if (sText != null)
            {   /* Read: the shown text replaced; the raw (what is   */
                /* written back) was never touched.  A null cannot   */
                /* happen -- the bytes already read once -- and      */
                /* would leave the UTF-16 reading.                   */
                node.Vn_sText = sText;
            }
        }
    }

    /*------------------------------------------------------------------------*/
    /* VRG_IsAnsiDocument:                                                    */
    /*                                                                        */
    /* Whether a registry document is a REGEDIT4 file (ANSI strings), for the */
    /* dispatcher, which holds documents rather than their root nodes.        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc : the document.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true for REGEDIT4.                                          */
    /*------------------------------------------------------------------------*/
    public static bool VRG_IsAnsiDocument(VoorheesDocument doc)
    {
        /* Judged by its header, as everywhere else in this module. */
        return(Vrg_IsAnsi(doc.Vd_root));
    }

    /*------------------------------------------------------------------------*/
    /* VRG_AnsiProblem:                                                       */
    /*                                                                        */
    /* Why a value cannot be stored in a REGEDIT4 file, if it cannot: an      */
    /* Expandable String or Multi-String is written as bytes of the system's  */
    /* ANSI code page, and a character that page does not hold would be       */
    /* written as "?" or a look-alike -- the saved value then differs from    */
    /* the one typed, without a word.  Such text is refused, naming the       */
    /* character and the code page.  Other kinds hold no such text.           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     node : the value made from the user's text.                        */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the reason, or null when the value can be stored.         */
    /*------------------------------------------------------------------------*/
    public static string VRG_AnsiProblem(VoorheesNode node)
    {
        Encoding strict;                                 // the ANSI page, throwing on what it cannot hold

        if (node == null || (node.Vn_iKind != VRG_EXPANDSZ && node.Vn_iKind != VRG_MULTISZ))
        {   /* Not a string stored in hex: nothing to check. */
            return(null);
        }

        /* The code page the save will use, built to throw on a character   */
        /* it cannot hold rather than to substitute one.                    */
        strict = Vrg_StringEncoding(true);
        try
        {   /* Every character must encode. */
            strict.GetBytes(node.Vn_sText);

            /* All of it encoded: nothing to refuse. */
            return(null);
        }
        catch (EncoderFallbackException exChar)
        {   /* One that cannot: named, with the page. */
            return("A REGEDIT4 file stores this text in the system's code page ("
                + strict.WebName + "), which has no character U+"
                + ((int)exChar.CharUnknown).ToString("X4", CultureInfo.InvariantCulture)
                + "; the saved value would not be the one typed.");
        }
    }

    /*------------------------------------------------------------------------*/
    /* VRG_AfterRawEdit:                                                      */
    /*                                                                        */
    /* An item a Raw edit just parsed, in a REGEDIT4 document: its hex(2) and */
    /* hex(7) strings read again as ANSI, as a whole-file load does           */
    /* (Vrg_RereadStrings) -- the parse alone reads them as UTF-16, so the    */
    /* Value box and the tree would show the bytes as garbage.  A key's       */
    /* values and a single value alike; a regedit 5 document needs nothing.   */
    /* The document's header decides, never the fragment (which has none).    */
    /* Called by the line engine's Raw edit (VoorheesLines.cs).               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc  : the document edited.                                        */
    /*     item : the item parsed from the Raw box.                           */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : its strings read as the file's encoding.                    */
    /*------------------------------------------------------------------------*/
    public static void VRG_AfterRawEdit(VoorheesDocument doc, VoorheesItem item)
    {
        if (!Vrg_IsAnsi(doc.Vd_root) || item.Vi_node == null)
        {   /* regedit 5 (UTF-16 is right), or a comment: nothing to read. */
            return;
        }

        if (item.Vi_node.Vn_IsContainer())
        {   /* A key with its values. */
            Vrg_RereadStrings(item.Vi_node);
        }
        else
        {   /* One value. */
            Vrg_RereadValue(item.Vi_node);
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_SaveBytes:                                                         */
    /*                                                                        */
    /* A registry document to the bytes of its file, in its own encoding.     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc : the document.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     byte[] : the file's new contents.                                  */
    /*------------------------------------------------------------------------*/
    public static byte[] Vrg_SaveBytes(VoorheesDocument doc)
    {
        /* Text by the line engine (header line first), bytes in the       */
        /* encoding and mark the file was read with -- for a REGEDIT4      */
        /* file's hex(2) / hex(7) strings that is not the question: they   */
        /* are bytes in the text already.  Called by the dispatcher        */
        /* (VoorheesFormat.cs, Vf_SaveBytes).                              */
        return(VoorheesText.Vtx_Encode(VoorheesLines.Vln_Write(doc),
            doc.Vd_encoding, doc.Vd_bBom));
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_NewDocument:                                                       */
    /*                                                                        */
    /* A new registry document: UTF-16 LE with a byte order mark, CRLF, the   */
    /* header line, no keys yet -- regedit 5's own form, as measured.         */
    /* Reached from the main window's New for a .reg (VoorheesDocument.cs     */
    /* Vd_CreateNew, through VoorheesFormat.cs Vf_NewDocument).               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesDocument : the document.                                   */
    /*------------------------------------------------------------------------*/
    public static VoorheesDocument Vrg_NewDocument()
    {
        VoorheesNode root;                               // the document node with its header

        /* The document node with regedit 5's header line and no items.   */
        /* Vn_bOneLine is already false from Vn_NewContainer, set again   */
        /* here (why is not recorded).                                    */
        root = VoorheesNode.Vn_NewContainer(VoorheesNode.VN_DOCUMENT);
        root.Vn_bOneLine = false;
        root.Vn_sRaw = VRG_HEADER5;

        /* regedit's own encoding (UTF-16 little-endian, with its byte      */
        /* order mark: the two arguments, and bBom true) and line ending.   */
        /* The closing text is generated: the header's line break and a     */
        /* blank line (VoorheesLines.cs, Vln_CloseOf).                      */
        return(VoorheesDocument.Vd_Create(VoorheesFormat.VF_REG, root, null,
            new UnicodeEncoding(false, true), true, "\r\n", null, null, null));
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_KindName:                                                          */
    /*                                                                        */
    /* A kind's name, for the Type box, menus and messages.                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iKind : a VRG_ kind.                                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the name; "Value" for anything else.                      */
    /*------------------------------------------------------------------------*/
    public static string Vrg_KindName(int iKind)
    {
        switch (iKind)
        {   /* One name per REG kind, after regedit's own type names.   */
            /* REG_SZ.                                                  */
            case VRG_SZ:          return("String");
            /* REG_EXPAND_SZ, written hex(2). */
            case VRG_EXPANDSZ:    return("Expandable String");
            /* REG_MULTI_SZ, written hex(7). */
            case VRG_MULTISZ:     return("Multi-String");
            /* REG_DWORD. */
            case VRG_DWORD:       return("DWORD");
            /* REG_QWORD, written hex(b). */
            case VRG_QWORD:       return("QWORD");
            /* REG_BINARY, written hex:. */
            case VRG_BINARY:      return("Binary");
            /* Any other hex(N). */
            case VRG_HEXOTHER:    return("Other Hex");
            /* "name"=- */
            case VRG_DELETEVALUE: return("Delete Value");
            /* [path] */
            case VRG_KEY:         return("Key");
            /* [-path] */
            case VRG_DELETEKEY:   return("Delete Key");
            /* Not a REG kind: a guard (the dispatcher sends REG's own   */
            /* kinds for a REG document), answering a plain word.        */
            default:              return("Value");
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_ChildKinds:                                                        */
    /*                                                                        */
    /* What Add Child offers: keys (and keys to delete) at the top level;     */
    /* every value kind but Other Hex in a key; nothing but comments under a  */
    /* key being deleted.                                                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     container : the container.                                         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     List<int> : the kinds, in menu order (a new list).                 */
    /*------------------------------------------------------------------------*/
    public static List<int> Vrg_ChildKinds(VoorheesNode container)
    {
        List<int> lstKinds;                              // the kinds

        /* A new list each time, filled by what the container is. */
        lstKinds = new List<int>();
        if (container.Vn_iKind == VoorheesNode.VN_DOCUMENT)
        {   /* The top level: keys only -- the file is a flat list of keys   */
            /* (Vrg_FitItem refuses one anywhere else).                      */
            lstKinds.Add(VRG_KEY);
            lstKinds.Add(VRG_DELETEKEY);
        }
        else if (container.Vn_iKind == VRG_KEY)
        {   /* A key: values, in the order regedit's New menu lists them   */
            /* (as found).  Other Hex is left out: there is nothing to     */
            /* make one from.                                              */
            lstKinds.Add(VRG_SZ);
            lstKinds.Add(VRG_BINARY);
            lstKinds.Add(VRG_DWORD);
            lstKinds.Add(VRG_QWORD);
            lstKinds.Add(VRG_MULTISZ);
            lstKinds.Add(VRG_EXPANDSZ);
            lstKinds.Add(VRG_DELETEVALUE);
        }

        /* The kinds that fit (none under a key being deleted, where only     */
        /* comments may go -- Add Comment is offered apart from this list).   */
        return(lstKinds);
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_PanelKinds:                                                        */
    /*                                                                        */
    /* What the Type box offers: a key is a Key or a Delete Key; a value any  */
    /* value kind (Other Hex only for a value that is one).                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     node : the entry's value.                                          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     List<int> : the kinds (a new list).                                */
    /*------------------------------------------------------------------------*/
    public static List<int> Vrg_PanelKinds(VoorheesNode node)
    {
        List<int> lstKinds;                              // the kinds

        /* A new list each time, filled by what the value is. */
        lstKinds = new List<int>();
        if (node.Vn_IsContainer())
        {   /* A key: it can switch between being kept and being deleted   */
            /* (Vrg_ValueFromPanel refuses the switch while it holds       */
            /* values), never become a value.                              */
            lstKinds.Add(VRG_KEY);
            lstKinds.Add(VRG_DELETEKEY);
            return(lstKinds);
        }

        /* A value: every value kind, in the Add menu's order. */
        lstKinds.Add(VRG_SZ);
        lstKinds.Add(VRG_BINARY);
        lstKinds.Add(VRG_DWORD);
        lstKinds.Add(VRG_QWORD);
        lstKinds.Add(VRG_MULTISZ);
        lstKinds.Add(VRG_EXPANDSZ);
        lstKinds.Add(VRG_DELETEVALUE);
        if (node.Vn_iKind == VRG_HEXOTHER)
        {   /* Its own kind too: an Other Hex value can stay one.  Why no   */
            /* other value is offered it is not recorded.                   */
            lstKinds.Add(VRG_HEXOTHER);
        }

        /* The value kinds. */
        return(lstKinds);
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_ValueFromPanel:                                                    */
    /*                                                                        */
    /* The value the edit panel describes: a value of the kind chosen from    */
    /* the Value box (unchanged kind and text keep it exactly as written), or */
    /* a key becoming a key to delete (only when it holds no values) or back. */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iKind   : the VRG_ kind chosen.                                    */
    /*     sText   : the Value box's text.                                    */
    /*     oldNode : the value being replaced.                                */
    /*     sError  : set to why the value is refused, else null.              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNode : the value, or null when refused.                    */
    /*------------------------------------------------------------------------*/
    public static VoorheesNode Vrg_ValueFromPanel(int iKind, string sText,
        VoorheesNode oldNode, out string sError)
    {
        VoorheesNode container;                          // a key of the other kind
        int i;

        /* No objection until one is found. */
        sError = null;
        if (oldNode.Vn_IsContainer())
        {   /* A key: only its kind (kept or deleted) can change. */
            if (iKind == oldNode.Vn_iKind)
            {   /* Unchanged: the very node handed back, values and all. */
                return(oldNode);
            }
            if (iKind != VRG_KEY && iKind != VRG_DELETEKEY)
            {   /* A key can't become a value -- a guard: Vrg_PanelKinds   */
                /* offers a key only the two key kinds.                    */
                sError = "A key can't become a value.";
                return(null);
            }
            for (i = 0; i < oldNode.Vn_Count(); i++)
            {   /* One item of the key per iteration: a value is what a key   */
                /* to delete cannot hold (Vrg_LineProblem would refuse it     */
                /* on the next open).                                         */
                if (iKind == VRG_DELETEKEY && oldNode.Vn_lstItems[i].Vi_IsValue())
                {   /* Values would go nowhere: refused, saying what to do. */
                    sError = "A key to delete holds no values: delete its values "
                        + "first.";
                    return(null);
                }
            }
            /* A new container of the other kind holding the same items      */
            /* (only comments, on the way to a key to delete) and the same   */
            /* closing text.                                                 */
            container = VoorheesNode.Vn_NewContainer(iKind);
            container.Vn_sClose = oldNode.Vn_sClose;
            container.Vn_lstItems.AddRange(oldNode.Vn_lstItems);
            return(container);
        }

        if (iKind == VRG_KEY || iKind == VRG_DELETEKEY)
        {   /* A value can't become a key -- a guard, as Vrg_PanelKinds   */
            /* offers a value no key kind.                                */
            sError = "A value can't become a key; add a key at the top level.";
            return(null);
        }

        /* The same kind and the same text (line endings aside): nothing   */
        /* changed, so the old node comes back and its raw text is kept,   */
        /* rather than being regenerated in regedit's form.                */
        if (iKind == oldNode.Vn_iKind && sText.Replace("\r\n", "\n")
            .Replace('\r', '\n') == oldNode.Vn_sText)
        {   /* The same value: exactly as it was written. */
            return(oldNode);
        }

        /* A value of the kind, or refused with the reason. */
        return(Vrg_MakeValue(iKind, sText, out sError));
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_NewValue:                                                          */
    /*                                                                        */
    /* An empty value of a kind: "" for the strings and binary, 0 for the     */
    /* numbers, an empty key.  Used by Vrg_NewChild (Add Child); its other    */
    /* caller, VoorheesFormat.cs Vf_NewValue, is called by nothing in this    */
    /* program, so the Other Hex case is never reached either.                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iKind : a VRG_ kind.                                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNode : the value.                                          */
    /*------------------------------------------------------------------------*/
    public static VoorheesNode Vrg_NewValue(int iKind)
    {
        VoorheesNode container;                          // a new key

        if (iKind == VRG_KEY || iKind == VRG_DELETEKEY)
        {   /* An empty key, its closing text "" as a read key's is (a key   */
            /* has none: Vrg_ParseLine).                                     */
            container = VoorheesNode.Vn_NewContainer(iKind);
            container.Vn_sClose = "";
            return(container);
        }

        if (iKind == VRG_DWORD)
        {   /* Zero, in the Value box's 0x-and-eight-digits form. */
            return(VoorheesNode.Vn_NewLeaf(iKind, "0x00000000"));
        }

        if (iKind == VRG_QWORD)
        {   /* Zero, in the 0x-and-sixteen-digits form. */
            return(VoorheesNode.Vn_NewLeaf(iKind, "0x0000000000000000"));
        }

        if (iKind == VRG_HEXOTHER)
        {   /* No bytes of type 0: the text carries the type, so it is not   */
            /* left empty.                                                   */
            return(VoorheesNode.Vn_NewLeaf(iKind, "hex(0):"));
        }

        /* Empty: a string, multi-string, binary or a delete -- written   */
        /* "", hex(..): with no bytes, or "-".                            */
        return(VoorheesNode.Vn_NewLeaf(iKind, ""));
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_ConvertText:                                                       */
    /*                                                                        */
    /* A value of the same kind from edited text (in-place editing, Replace): */
    /* the same text keeps it as written; other text must be valid for the    */
    /* kind; keys and values to delete have no text.  Called through          */
    /* VoorheesFormat.cs (Vf_ConvertText).                                    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     oldNode : the value being replaced.                                */
    /*     sText   : the new text.                                            */
    /*     sReason : set to Vrg_MakeValue's reason when it refuses the text   */
    /*               for the value's type; "" otherwise.                      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNode : the new value, or null when refused.                */
    /*------------------------------------------------------------------------*/
    public static VoorheesNode Vrg_ConvertText(VoorheesNode oldNode, string sText, out string sReason)
    {
        string sError;                                   // Vrg_MakeValue's reason for a refusal
        VoorheesNode nodNew;                             // the value made, or null

        /* No reason until a refusal has one. */
        sReason = "";

        if (oldNode.Vn_IsContainer() || oldNode.Vn_iKind == VRG_DELETEVALUE)
        {   /* A key, or a value to delete: no text to change, refused, with   */
            /* no reason of its own.                                           */
            return(null);
        }

        if (sText.Replace("\r\n", "\n").Replace('\r', '\n') == oldNode.Vn_sText)
        {   /* Unchanged (line endings aside): the very node, so its raw   */
            /* text is kept rather than regenerated.                       */
            return(oldNode);
        }

        /* A valid value of its kind, or null with the reason. */
        nodNew = Vrg_MakeValue(oldNode.Vn_iKind, sText, out sError);
        if (nodNew == null && sError != null)
        {   /* Refused: its reason passed on, which the in-place edit   */
            /* shows in place of a pointer to the Type box.             */
            sReason = sError;
        }

        /* The value, or null when refused. */
        return(nodNew);
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_NewChild:                                                          */
    /*                                                                        */
    /* Add Child's new item: a key under HKEY_CURRENT_USER\Software with a    */
    /* free name, or a value named with the first free variant of             */
    /* "NewValue".                                                            */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc       : the document (for free names).                         */
    /*     container : the container.                                         */
    /*     iKind     : a VRG_ kind.                                           */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesItem : the new item.                                       */
    /*------------------------------------------------------------------------*/
    public static VoorheesItem Vrg_NewChild(VoorheesDocument doc,
        VoorheesNode container, int iKind)
    {
        /* A free name for the kind: the base for the container (a key path   */
        /* at the top level, "NewValue" in a key), with 1, 2, 3 ... added     */
        /* until no member of the container has it, names compared without    */
        /* case as REG compares them (Vd_UnusedKey); and an empty value of    */
        /* the kind.                                                          */
        return(VoorheesItem.Vi_NewMember(doc.Vd_UnusedKey(container,
            Vrg_NewKeyBase(container)), Vrg_NewValue(iKind)));
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_NewKeyBase:                                                        */
    /*                                                                        */
    /* The name a new item starts from: a key path at the top level, else     */
    /* "NewValue".                                                            */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     container : the container.                                         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the base name.                                            */
    /*------------------------------------------------------------------------*/
    public static string Vrg_NewKeyBase(VoorheesNode container)
    {
        if (container.Vn_iKind == VoorheesNode.VN_DOCUMENT)
        {   /* The top level, where only keys go: a key path under a hive. */
            return(VRG_NEWKEYBASE);
        }

        /* Inside a key, where only values go: a value's name. */
        return(VRG_NEWVALUEBASE);
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_NameQuestion:                                                      */
    /*                                                                        */
    /* What Add asks before it adds a key (or a key to delete): the hive,     */
    /* chosen from a drop-down of the five full hive names (VRG_HIVES), and   */
    /* the path under it, starting at a free name -- HKEY_CURRENT_USER and    */
    /* "Software\NewKey" (or NewKey1, 2 ...: the first not in the file).      */
    /* Values ask nothing (they get a placeholder name).  Called through      */
    /* VoorheesFormat.cs (Vf_NameQuestion) by the editor's Add                */
    /* (JsonEditForm.cs).                                                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc   : the document (for the free name).                          */
    /*     iKind : the kind being added.                                      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNameQuestion : the question, or null for a value.          */
    /*------------------------------------------------------------------------*/
    public static VoorheesNameQuestion Vrg_NameQuestion(VoorheesDocument doc, int iKind)
    {
        string sFree;                                    // a free key path under the default hive
        string sTitle;                                   // the dialog's title
        int iHive;                                       // the default hive's entry

        if (iKind != VRG_KEY && iKind != VRG_DELETEKEY)
        {   /* A value: a placeholder name will do -- no question (null). */
            return(null);
        }

        /* The dialog's title says which kind of key is being added. */
        sTitle = "Add Key";
        if (iKind == VRG_DELETEKEY)
        {   /* A key the file deletes. */
            sTitle = "Add Key to Delete";
        }

        /* The default: a free path at the top level (VRG_NEWKEYBASE with    */
        /* a number if taken), split into its hive -- always found, as the   */
        /* base starts with HKEY_CURRENT_USER -- and the rest after the      */
        /* backslash, which the path box starts with.                        */
        sFree = doc.Vd_UnusedKey(doc.Vd_root, VRG_NEWKEYBASE);
        iHive = Vrg_HiveIndex(sFree);
        return(VoorheesNameQuestion.Vnq_CreateChoice(sTitle, "Hive:", VRG_HIVES, iHive,
            "Key path under the hive:", sFree.Substring(VRG_HIVES[iHive].Length + 1)));
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_RenameQuestion:                                                    */
    /*                                                                        */
    /* What Rename Key asks for a key (or a key to delete): the same hive     */
    /* drop-down and path box as adding one (Vrg_NameQuestion), starting at   */
    /* the key as it is -- its hive chosen, the rest of its path in the box.  */
    /* A key not under any hive (a file regedit would refuse) starts at       */
    /* HKEY_CURRENT_USER with its whole path in the box.  A value's name asks */
    /* nothing special (the plain box).  Called through VoorheesFormat.cs     */
    /* (Vf_RenameQuestion) by the editor's Rename (JsonEditForm.cs).          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     item : the entry being renamed.                                    */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNameQuestion : the question, or null for a value.          */
    /*------------------------------------------------------------------------*/
    public static VoorheesNameQuestion Vrg_RenameQuestion(VoorheesItem item)
    {
        string sPath;                                    // the key's path under its hive
        string sTitle;                                   // the dialog's title
        int iHive;                                       // its hive's entry

        if (item.Vi_iKind != VoorheesItem.VI_MEMBER || !item.Vi_node.Vn_IsContainer())
        {   /* A value (or a comment, tested first so a comment's missing   */
            /* node is never looked at): the plain box (null).              */
            return(null);
        }

        /* The dialog's title says which kind of key is being renamed. */
        sTitle = "Rename Key";
        if (item.Vi_node.Vn_iKind == VRG_DELETEKEY)
        {   /* A key the file deletes. */
            sTitle = "Rename Key to Delete";
        }

        /* The key's path split into its hive and the rest. */
        iHive = Vrg_HiveIndex(item.Vi_sKey);
        if (iHive >= 0)
        {   /* Under a hive: the rest of the path (nothing for the hive itself). */
            sPath = "";
            if (item.Vi_sKey.Length > VRG_HIVES[iHive].Length)
            {   /* Something below it: from after the backslash that   */
                /* Vrg_HiveIndex found right after the hive's name.    */
                sPath = item.Vi_sKey.Substring(VRG_HIVES[iHive].Length + 1);
            }
        }
        else
        {   /* Not under a hive: the new-key base's hive, the whole path to fix. */
            iHive = Vrg_HiveIndex(VRG_NEWKEYBASE);
            sPath = item.Vi_sKey;
        }

        /* The question, starting at the key as it is. */
        return(VoorheesNameQuestion.Vnq_CreateChoice(sTitle, "Hive:", VRG_HIVES, iHive,
            "Key path under the hive:", sPath));
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_NameFromAnswer:                                                    */
    /*                                                                        */
    /* A key path from the hive chosen and the path typed under it: joined    */
    /* with one "\" (backslashes at either end of the typed path dropped; no  */
    /* path gives the hive itself).  A typed path that already starts with a  */
    /* hive (a full path pasted in) is taken as it is.  Anything else wrong   */
    /* with it (a "]", a line break) is refused when the name is given        */
    /* (Vrg_KeyProblem).  Called through VoorheesFormat.cs                    */
    /* (Vf_NameFromAnswer) once the Add or Rename question is answered.       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sHive : the hive's full name.                                      */
    /*     sPath : the path typed.                                            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the key path.                                             */
    /*------------------------------------------------------------------------*/
    public static string Vrg_NameFromAnswer(string sHive, string sPath)
    {
        string sRest;                                    // the typed path without outer backslashes

        if (Vrg_HasHive(sPath))
        {   /* A full path already: as typed -- its own hive wins over the   */
            /* drop-down's, and nothing is trimmed from it.                  */
            return(sPath);
        }

        /* The typed path with backslashes at either end dropped, so the   */
        /* join below never doubles one or leaves one at the end.          */
        sRest = sPath.Trim('\\');
        if (sRest.Length == 0)
        {   /* Nothing under it: the hive itself. */
            return(sHive);
        }

        /* Hive, separator, path. */
        return(sHive + "\\" + sRest);
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_HiveIndex:                                                         */
    /*                                                                        */
    /* Which of VRG_HIVES a key path starts with.                             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sPath : the path.                                                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : its entry in VRG_HIVES, or -1 for none (compared without     */
    /*           case; the hive must be followed by nothing or "\").          */
    /*------------------------------------------------------------------------*/
    static int Vrg_HiveIndex(string sPath)
    {
        int i;

        for (i = 0; i < VRG_HIVES.Length; i++)
        {   /* One hive per iteration.  The character after its name must   */
            /* be the end or a "\", so HKEY_USERSX is not HKEY_USERS.       */
            if (sPath.StartsWith(VRG_HIVES[i], StringComparison.OrdinalIgnoreCase)
                && (sPath.Length == VRG_HIVES[i].Length
                || sPath[VRG_HIVES[i].Length] == '\\'))
            {   /* This hive. */
                return(i);
            }
        }

        /* None. */
        return(-1);
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_FitItem:                                                           */
    /*                                                                        */
    /* Whether an item can go into a container: comments anywhere; keys at    */
    /* the top level only (a flat list: each key carries its full path, as    */
    /* regedit writes them); values in a key, not under a key to delete;      */
    /* every entry needs a name.  Called through VoorheesFormat.cs for an     */
    /* insert or paste (Vf_FitItem) and for a value about to change           */
    /* (Vf_CheckItem).                                                        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     container : where it goes.                                         */
    /*     item      : the item.                                              */
    /*     sError    : set to why it cannot go there, else null.              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesItem : the item, or null when refused.                     */
    /*------------------------------------------------------------------------*/
    public static VoorheesItem Vrg_FitItem(VoorheesNode container,
        VoorheesItem item, out string sError)
    {
        /* No objection until one is found.  The same three placement rules   */
        /* as Vrg_LineProblem applies to a file, so an item that fits here    */
        /* also reads back.                                                   */
        sError = null;
        if (item.Vi_iKind == VoorheesItem.VI_COMMENT)
        {   /* Comments go anywhere (and have no node to look at below). */
            return(item);
        }

        if (item.Vi_iKind == VoorheesItem.VI_ELEMENT)
        {   /* No name -- a value from a JSON array, say: a .reg line has   */
            /* none to write.  Refused.                                     */
            sError = "Registry values need a name.";
            return(null);
        }

        if (item.Vi_node.Vn_IsContainer() && container.Vn_iKind != VoorheesNode.VN_DOCUMENT)
        {   /* A key inside a key: keys do not nest in the file, each line   */
            /* carries the full path.  Refused.                              */
            sError = "Keys are listed at the top level, each with its full path.";
            return(null);
        }

        if (!item.Vi_node.Vn_IsContainer() && container.Vn_iKind == VoorheesNode.VN_DOCUMENT)
        {   /* A value outside any key: it would belong to no key. */
            sError = "A value must be inside a key.";
            return(null);
        }

        if (!item.Vi_node.Vn_IsContainer() && container.Vn_iKind == VRG_DELETEKEY)
        {   /* A value under a key to delete: meaningless, and refused on   */
            /* the next open (Vrg_LineProblem).                             */
            sError = "A key being deleted holds no values.";
            return(null);
        }

        /* It fits, unchanged. */
        return(item);
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_KeyProblem:                                                        */
    /*                                                                        */
    /* Why a new name would not do: one line; a key path starts with a full   */
    /* hive name (HKEY_...) and holds no "]".  A value name may be anything   */
    /* on one line ("" makes it the default value, "@").  A repeat is refused */
    /* by VoorheesDocument.cs itself.  Called through VoorheesFormat.cs       */
    /* (Vf_KeyProblem) for an insert and a rename.                            */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     item    : the entry (its value says what it is).                   */
    /*     sNewKey : the proposed name.                                       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the reason, or null when it is acceptable.                */
    /*------------------------------------------------------------------------*/
    public static string Vrg_KeyProblem(VoorheesItem item, string sNewKey)
    {
        if (sNewKey.IndexOf('\r') >= 0 || sNewKey.IndexOf('\n') >= 0)
        {   /* A line break, in a key path or a value name: the line would   */
            /* end there.                                                    */
            return("A name is one line.");
        }

        if (!item.Vi_node.Vn_IsContainer())
        {   /* A value name: anything else goes -- Vrg_Quote escapes "\"   */
            /* and quotes, and "" is written "@".                          */
            return(null);
        }

        if (sNewKey.IndexOf(']') >= 0)
        {   /* A "]" in a key path: refused.  Reading takes the path up to   */
            /* the LAST "]" (Vrg_ParseLine), so one would read back; why     */
            /* it is refused all the same is not recorded.                   */
            return("A key path can't contain \"]\".");
        }

        if (!Vrg_HasHive(sNewKey))
        {   /* Not under a hive: regedit would not import it (as found). */
            return("A key path starts with a full hive name: "
                + string.Join(", ", VRG_HIVES) + ".");
        }

        /* Acceptable. */
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_HasHive:                                                           */
    /*                                                                        */
    /* Whether a key path starts with a full hive name (then nothing or "\"). */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sPath : the path.                                                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when it does (compared without case).                  */
    /*------------------------------------------------------------------------*/
    static bool Vrg_HasHive(string sPath)
    {
        /* One of the hives, found by Vrg_HiveIndex (-1 for none), so the   */
        /* two always agree on what counts as a hive.                       */
        return(Vrg_HiveIndex(sPath) >= 0);
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_CountBadHives:                                                     */
    /*                                                                        */
    /* How many keys do not start with a full hive name (regedit refuses to   */
    /* import them), for the open warning.                                    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     root : the document node.                                          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the count.                                                   */
    /*------------------------------------------------------------------------*/
    static int Vrg_CountBadHives(VoorheesNode root)
    {
        int iCount;                                      // keys found so far
        int i;

        /* The top level only: keys never nest (Vrg_FitItem), so every key   */
        /* is a top-level item.  Vi_IsValue first, so a comment's missing    */
        /* node is never looked at.                                          */
        iCount = 0;
        for (i = 0; i < root.Vn_Count(); i++)
        {   /* One top-level item per iteration. */
            if (root.Vn_lstItems[i].Vi_IsValue()
                && root.Vn_lstItems[i].Vi_node.Vn_IsContainer()
                && !Vrg_HasHive(root.Vn_lstItems[i].Vi_sKey))
            {   /* A key not under a hive: one more. */
                iCount++;
            }
        }

        /* Every one. */
        return(iCount);
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_OpenWarnings:                                                      */
    /*                                                                        */
    /* What the user should be told when a registry file opens: repeated keys */
    /* or values (the last copy wins on import), keys not under a hive.       */
    /* Called through VoorheesFormat.cs (Vf_OpenWarnings) by the editor as it */
    /* shows a file it has read (JsonEditForm.cs).                            */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc          : the document.                                       */
    /*     sFileName    : its file's name (no folder).                        */
    /*     iExtraCopies : repeated-name copies (compared without case).       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     List<string> : the messages (empty for none).                      */
    /*------------------------------------------------------------------------*/
    public static List<string> Vrg_OpenWarnings(VoorheesDocument doc,
        string sFileName, int iExtraCopies)
    {
        List<string> lstWarnings;                        // the messages
        int iBad;                                        // keys not under a hive

        /* A new list, empty unless something below is worth saying. */
        lstWarnings = new List<string>();
        if (iExtraCopies > 0)
        {   /* Something repeats: how many copies, that names compare        */
            /* without case, which copy an import keeps (the LAST, as        */
            /* found), and that this editor keeps and saves every copy --    */
            /* the "(duplicate)" mark is the tree's (VoorheesTreeView.cs).   */
            lstWarnings.Add(sFileName + " repeats a key or a value ("
                + iExtraCopies.ToString(CultureInfo.InvariantCulture)
                + " extra copies or more; names are compared without case)."
                + "\r\n\r\nWhen the file is imported the LAST copy wins.  This editor "
                + "keeps every copy, marks them \"(duplicate)\" in the tree and "
                + "saves them all back.");
        }

        /* Keys whose path starts with no full hive name. */
        iBad = Vrg_CountBadHives(doc.Vd_root);
        if (iBad > 0)
        {   /* regedit would refuse those keys (as found): said, with the   */
            /* hive names a path should start with.                         */
            lstWarnings.Add(iBad.ToString(CultureInfo.InvariantCulture)
                + " key(s) in " + sFileName + " do not start with a full hive "
                + "name (HKEY_LOCAL_MACHINE, HKEY_CURRENT_USER ...); regedit will "
                + "not import them.");
        }

        /* Every warning, or none: the caller shows whatever is here. */
        return(lstWarnings);
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_ValidateWarnings:                                                  */
    /*                                                                        */
    /* The dispatcher's validation warnings, one line each: repeats, keys not */
    /* under a hive.  NOTHING IN THIS PROGRAM REACHES IT: its one caller,     */
    /* VoorheesFormat.cs Vf_ValidateWarnings, is itself called by nothing.    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc          : the document.                                       */
    /*     iExtraCopies : repeated-name copies.                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     List<string> : the warnings (empty for none).                      */
    /*------------------------------------------------------------------------*/
    public static List<string> Vrg_ValidateWarnings(VoorheesDocument doc,
        int iExtraCopies)
    {
        List<string> lstWarnings;                        // the warnings
        int iBad;                                        // keys not under a hive

        /* The same two checks as Vrg_OpenWarnings, worded as one line each. */
        lstWarnings = new List<string>();
        if (iExtraCopies > 0)
        {   /* Repeats: how many, and which copy an import keeps. */
            lstWarnings.Add(iExtraCopies.ToString(CultureInfo.InvariantCulture)
                + " repeated key(s) or value(s) (compared without case); the last "
                + "copy wins on import");
        }

        /* Keys whose path starts with no full hive name. */
        iBad = Vrg_CountBadHives(doc.Vd_root);
        if (iBad > 0)
        {   /* Not importable (as found). */
            lstWarnings.Add(iBad.ToString(CultureInfo.InvariantCulture)
                + " key(s) not under a hive; regedit will not import them");
        }

        /* Every warning, or none. */
        return(lstWarnings);
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_LabelValue:                                                        */
    /*                                                                        */
    /* A value as its tree label shows it: numbers "0x0000001f (31)", a value */
    /* to delete "(delete)", anything else its text.                          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     node : the value.                                                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the label's value part.                                   */
    /*------------------------------------------------------------------------*/
    public static string Vrg_LabelValue(VoorheesNode node)
    {
        if (node.Vn_iKind == VRG_DWORD || node.Vn_iKind == VRG_QWORD)
        {   /* Hex and decimal, as regedit's own list shows a number (as   */
            /* found): the stored 0x text, then its value in brackets.     */
            return(node.Vn_sText + " (" + Vrg_ParseNumber(node.Vn_sText,
                node.Vn_iKind == VRG_QWORD).ToString(CultureInfo.InvariantCulture)
                + ")");
        }

        if (node.Vn_iKind == VRG_DELETEVALUE)
        {   /* Nothing to show but what it does. */
            return("(delete)");
        }

        /* The text as the Value box has it: a string, pairs, or a   */
        /* multi-string's lines.  Called through VoorheesFormat.cs   */
        /* (Vf_LabelValue) by the tree.                              */
        return(node.Vn_sText);
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_ItemName:                                                          */
    /*                                                                        */
    /* An entry's name in the tree: the default value "(Default)", anything   */
    /* else its name or path.                                                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     item : the entry.                                                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the name.                                                 */
    /*------------------------------------------------------------------------*/
    public static string Vrg_ItemName(VoorheesItem item)
    {
        if (item.Vi_iKind != VoorheesItem.VI_MEMBER)
        {   /* Unkeyed (never in a registry file): no name.  A guard. */
            return("");
        }

        if (item.Vi_sKey.Length == 0 && !item.Vi_node.Vn_IsContainer())
        {   /* "@": the empty name is the default value, shown as regedit   */
            /* shows it (as found).  An empty KEY path stays "" -- it is    */
            /* not a default anything.                                      */
            return("(Default)");
        }

        /* Its name, or a key's whole path.  Called through   */
        /* VoorheesFormat.cs (Vf_ItemName) by the tree.       */
        return(item.Vi_sKey);
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_WriteHeader:                                                       */
    /*                                                                        */
    /* A key's line: "[" (and "-" for a key to delete), the path, "]", then   */
    /* whatever followed it as written.                                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     item : the key.                                                    */
    /*     sb   : where the line goes.                                        */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the line is appended.                                       */
    /*------------------------------------------------------------------------*/
    public static void Vrg_WriteHeader(VoorheesItem item, StringBuilder sb)
    {
        /* "[", the "-" of a key to delete, the path, "]": the reverse of   */
        /* Vrg_ParseLine's key half.  Called by the line engine             */
        /* (VoorheesLines.cs, Vln_WriteHeaderLine).                         */
        sb.Append('[');
        if (item.Vi_node.Vn_iKind == VRG_DELETEKEY)
        {   /* Delete it: the kind's mark, before the path. */
            sb.Append('-');
        }
        sb.Append(item.Vi_sKey);
        sb.Append(']');
        if (item.Vi_sValueGap != null)
        {   /* Whatever followed the "]" when read, back as written. */
            sb.Append(item.Vi_sValueGap);
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_ResetLayout:                                                       */
    /*                                                                        */
    /* Clears a copied item's layout (Paste): lead, gaps, the quoted name and */
    /* the raw value (regenerated in regedit's form), inside a key too.       */
    /* ONLY for a copy not in a document: it changes the item in place with   */
    /* no undo step.  Its caller (VoorheesDocument.cs, the paste, through     */
    /* VoorheesFormat.cs Vf_ResetLayout) hands it a private deep copy.        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     item : the copy.                                                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the layout fields are null.                                 */
    /*------------------------------------------------------------------------*/
    public static void Vrg_ResetLayout(VoorheesItem item)
    {
        int i;

        /* The layout every item has, set to null = "generate": the line   */
        /* engine's lead, regedit's "=", no trailing text.                 */
        item.Vi_sLead = null;
        item.Vi_sKeyGap = null;
        item.Vi_sValueGap = null;
        if (item.Vi_node == null)
        {   /* A comment: nothing more (its text is content and stays). */
            return;
        }

        if (!item.Vi_node.Vn_IsContainer())
        {   /* A value: name and value generated afresh from the name and   */
            /* the text, so a paste comes out in regedit's form -- and in   */
            /* the target file's string encoding (Vrg_GenerateValue).       */
            item.Vi_sKeyRaw = null;
            item.Vi_node.Vn_sRaw = null;
            return;
        }

        /* A key: no closing text ("" as a read key has), then its values   */
        /* the same way.                                                    */
        item.Vi_node.Vn_sClose = "";
        for (i = 0; i < item.Vi_node.Vn_lstItems.Count; i++)
        {   /* One item of the key per iteration (keys do not nest). */
            Vrg_ResetLayout(item.Vi_node.Vn_lstItems[i]);
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vrg_DescribeStyle:                                                     */
    /*                                                                        */
    /* The dispatcher's style summary for a registry file: which header it    */
    /* has.  NOTHING IN THIS PROGRAM REACHES IT: its one caller,              */
    /* VoorheesFormat.cs Vf_DescribeStyle, is itself called by nothing.       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc : the document.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : e.g. "regedit 5".                                         */
    /*------------------------------------------------------------------------*/
    public static string Vrg_DescribeStyle(VoorheesDocument doc)
    {
        if (Vrg_IsAnsi(doc.Vd_root))
        {   /* The old form, whose hex strings are ANSI. */
            return("REGEDIT4 (ANSI strings)");
        }

        /* The current form (or no header at all). */
        return("regedit 5");
    }
}
