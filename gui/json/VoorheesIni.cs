/*----------------------------------------------------------------------------*/
/* VoorheesIni.cs                                                             */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/*----------------------------------------------------------------------------*/
/* Classic Windows INI files: what an INI line means, how one is written, and */
/* INI's answers to the questions the dispatcher (VoorheesFormat) asks --     */
/* kinds, keys, values, comments, labels and warnings.  The line engine       */
/* (VoorheesLines.cs) does the reading, writing, rendering and Raw edits,     */
/* asking this module about single lines.                                     */
/*                                                                            */
/* THE RULES this module applies, modelled on how Windows' profile functions  */
/* read a file (as found: Windows' own reader is not in the tree to check     */
/* them against):                                                             */
/*     o a line whose first non-space character is ";" or "#" is a comment;   */
/*       there are NO inline comments: a ";" after a value is part of it;     */
/*     o "[" first, and a "]" somewhere after it, starts a section; its name  */
/*       is everything up to the FIRST "]", exactly as written; anything      */
/*       after the "]" is that line's comment (a "[" line without "]" is not  */
/*       a section: the two rules below read it, as an entry if it holds an   */
/*       "=", else as a key on its own);                                      */
/*     o otherwise the first "=" splits a line into key and value, spaces     */
/*       round both trimmed (they stay in the file, as the gaps);             */
/*     o a line with no "=" is a key on its own ("key only");                 */
/*     o keys and section names compare without case;                         */
/*     o entries before the first section are document-level ("global");      */
/*     o quoted values are shown and edited exactly as written, quotes        */
/*       included.                                                            */
/* INI is never invalid: any text reads.                                      */
/*                                                                            */
/* How a line maps onto the model is the line engine's (VoorheesLines.cs      */
/* header); here:                                                             */
/*     section : VI_MEMBER, Vi_sKey the name; value a VIN_SECTION container;  */
/*               after the "]": Vi_sOpenGap (spaces) + Vi_sOpenComment (the   */
/*               rest), or Vi_sValueGap when only spaces follow.              */
/*     entry   : VI_MEMBER, Vi_sKey the key; Vi_sKeyGap the spaces, "=" and   */
/*               spaces between key and value; value a VIN_TEXT leaf whose    */
/*               Vn_sText is the value as written (Vn_sRaw stays null: the    */
/*               text IS the token); Vi_sValueGap the trailing spaces.        */
/*     key only: VI_MEMBER with a VIN_KEYONLY leaf (text ""); trailing spaces */
/*               in Vi_sValueGap.                                             */
/*     comment : VI_COMMENT, Vi_sComment the line from its marker on.         */
/*                                                                            */
/* Class:                                                                     */
/*     VoorheesIni (Vin_) : static functions only; nothing is kept between    */
/*                          calls, so any of them may run on a worker thread. */
/*----------------------------------------------------------------------------*/

/* The framework parts used: the kind lists (Collections.Generic), the     */
/* invariant culture for the counts in messages (Globalization), and the   */
/* StringBuilder and Encoding of the writers and the loader (Text).        */
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

/*----------------------------------------------------------------------------*/
/* VoorheesIni                                                                */
/*                                                                            */
/* INI lines, kinds and rules.                                                */
/*----------------------------------------------------------------------------*/
static class VoorheesIni
{
    /*------------------------------------------------------------------------*/
    /* INI's kinds of node, in INI's range (20-29).  Every format has its own */
    /* decade (VN_DOCUMENT 1, JSON 10s, INI 20s, the Klipper family 30s, REG  */
    /* 40s), so one int names a kind in any document: the dispatcher tests    */
    /* kinds of several formats side by side (VoorheesFormat.cs, its import   */
    /* of an item from another format), and a value here must not collide.    */
    /*------------------------------------------------------------------------*/

    public const int VIN_TEXT = 20;                      // an entry's value: text, one line
    public const int VIN_KEYONLY = 21;                   // a key on a line of its own, no "=" and no value
    public const int VIN_SECTION = 22;                   // a [section] and the lines under it

    /*------------------------------------------------------------------------*/
    /* Defaults for generated text: what a new document uses, and what a read */
    /* file falls back on when it has no entry or no comment to copy the      */
    /* style from (Vin_LoadDocument).                                         */
    /*------------------------------------------------------------------------*/

    public const string VIN_DEFAULTDELIMITER = "=";      // between key and value, no spaces: key=value, as Windows writes an entry (as found)
    public const string VIN_DEFAULTCOMMENTPREFIX = ";";  // the comment marker Windows' own INI files use (as found); "#" is read too
    const string VIN_NEWKEYBASE = "newKey";              // a new entry's key, before 1, 2, 3 ... make it free
    const string VIN_NEWSECTIONBASE = "newSection";      // a new section's name, likewise

    /*------------------------------------------------------------------------*/
    /* Vin_ParseLine:                                                         */
    /*                                                                        */
    /* One line as an INI item (see the file header for the rules).  Its one  */
    /* caller is the line engine (VoorheesLines.cs, Vln_ParseLine), for every */
    /* line that is not blank; the engine relies on every character of the    */
    /* line landing in one of the item's fields, which is what lets the file  */
    /* be written back byte for byte.                                         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sContent : the line from its first non-space character to its line */
    /*                break (not empty, and not starting with a space or tab: */
    /*                the engine has moved the indentation into the lead).    */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesItem : the item; its lead is the line engine's to set.     */
    /*------------------------------------------------------------------------*/
    public static VoorheesItem Vin_ParseLine(string sContent)
    {
        VoorheesItem item;                               // the item made
        VoorheesNode section;                            // a section's container
        string sAfter;                                   // text after a section's "]"
        string sKeyPart;                                 // an entry's text before "="
        string sValuePart;                               // its text after "="
        string sKey;                                     // the key, trimmed
        string sValue;                                   // the value, trimmed
        int iClose;                                      // position of a section's "]"
        int iEquals;                                     // position of an entry's "="
        int iGapEnd;                                     // end of the spaces after "="

        if (sContent[0] == ';' || sContent[0] == '#')
        {   /* A comment line, kept whole -- marker, words and trailing       */
            /* spaces -- as the item's comment.  Tested FIRST, so a comment   */
            /* that holds a "[" or an "=" is never read as a section or an    */
            /* entry; and since the line starts at its first non-space        */
            /* character, [0] is the character that decides.                  */
            return(VoorheesItem.Vi_NewComment(sContent));
        }

        /* The first "]" anywhere on the line, for the section test below.    */
        /* A line is a section only when it starts "[" AND has a "]" after    */
        /* it; with "[" first, any "]" is at 1 or beyond, so "> 0" means      */
        /* "there is one".  A "[" line with none falls through to the entry   */
        /* rules below: an entry if it holds an "=", else a key on its own.   */
        iClose = sContent.IndexOf(']');
        if (sContent[0] == '[' && iClose > 0)
        {   /* A section: the name is everything up to the FIRST "]".           */
            /* The section's container and its member item.  The name is        */
            /* taken exactly as written, inner spaces included ("[ a ]" is      */
            /* named " a "); names compare without case only when looked up     */
            /* (VoorheesFormat.cs, Vf_KeyComparer).  Vn_sClose is stored as     */
            /* "" rather than left null for generating: an INI section has no   */
            /* closing text -- its last line's break belongs to the next        */
            /* item's lead (VoorheesLines.cs) -- so "" is exactly what the      */
            /* file holds there.                                                */
            section = VoorheesNode.Vn_NewContainer(VIN_SECTION);
            section.Vn_sClose = "";
            item = VoorheesItem.Vi_NewMember(sContent.Substring(1, iClose - 1),
                section);

            /* What follows the "]" on the header line, split two ways so the   */
            /* line writes back unchanged (Vin_WriteHeader reads the same       */
            /* fields): spaces alone go to Vi_sValueGap; anything else is the   */
            /* line's comment, with the spaces before it kept apart.            */
            sAfter = sContent.Substring(iClose + 1);
            if (Vin_TrimSpaces(sAfter).Length == 0)
            {   /* Only spaces or tabs after it, or nothing. */
                if (sAfter.Length > 0)
                {   /* Trailing spaces or tabs: stored, so the write gives   */
                    /* them back.  Nothing at all leaves the field null,     */
                    /* which writes nothing.                                 */
                    item.Vi_sValueGap = sAfter;
                }
            }
            else
            {   /* Text after it: the line's comment, whatever it starts with   */
                /* -- INI gives text after a "]" no other meaning.  Its         */
                /* leading spaces go to Vi_sOpenGap (possibly ""), so a read    */
                /* comment keeps its own spacing while a new one, with the      */
                /* gap null, gets one space when written.                       */
                item.Vi_sOpenGap = Vin_LeadingSpaces(sAfter);
                item.Vi_sOpenComment = sAfter.Substring(item.Vi_sOpenGap.Length);
            }

            /* The section, with no items yet: the line engine puts the lines   */
            /* that follow, up to the next section header, into it.             */
            return(item);
        }

        /* Neither a comment nor a section: the FIRST "=" splits the key    */
        /* from the value, so a value may hold "=" itself ("a=b=c" is the   */
        /* key "a" with the value "b=c").                                   */
        iEquals = sContent.IndexOf('=');
        if (iEquals < 0)
        {   /* No "=": a key on its own (VIN_KEYONLY, its text "").  Only   */
            /* the END can be trimmed -- the line starts at a non-space     */
            /* character -- so what the trim removed is exactly the tail,   */
            /* and it is kept apart for the write.                          */
            sKey = Vin_TrimSpaces(sContent);
            item = VoorheesItem.Vi_NewMember(sKey,
                VoorheesNode.Vn_NewLeaf(VIN_KEYONLY, ""));
            if (sKey.Length < sContent.Length)
            {   /* Spaces or tabs after the key: stored, so the write gives   */
                /* them back.                                                 */
                item.Vi_sValueGap = sContent.Substring(sKey.Length);
            }
            return(item);
        }

        /* An entry: the key is the text before the "=", trimmed; the value   */
        /* is the text after it, its leading spaces counted off (iGapEnd)     */
        /* and then its end trimmed.  The key part needs no leading trim,     */
        /* as the line starts at a non-space character.  A line starting      */
        /* "=" gives an empty key: reading never refuses a line, though       */
        /* Vin_KeyProblem will not let an empty key be typed.                 */
        sKeyPart = sContent.Substring(0, iEquals);
        sValuePart = sContent.Substring(iEquals + 1);
        sKey = Vin_TrimSpaces(sKeyPart);
        iGapEnd = Vin_LeadingSpaces(sValuePart).Length;
        sValue = Vin_TrimSpaces(sValuePart.Substring(iGapEnd));

        /* The member, its VIN_TEXT value the text exactly as written --     */
        /* quotes included, as INI gives them no meaning here.  Everything   */
        /* between key and value -- the spaces after the key, the "=", the   */
        /* spaces before the value -- is stored whole as Vi_sKeyGap, so a    */
        /* read line keeps its own spacing and Vin_WriteEntry only appends   */
        /* it back.                                                          */
        item = VoorheesItem.Vi_NewMember(sKey,
            VoorheesNode.Vn_NewLeaf(VIN_TEXT, sValue));
        item.Vi_sKeyGap = sKeyPart.Substring(sKey.Length) + "="
            + sValuePart.Substring(0, iGapEnd);
        if (iGapEnd + sValue.Length < sValuePart.Length)
        {   /* Spaces or tabs after the value: stored in Vi_sValueGap, so   */
            /* the write gives them back.                                   */
            item.Vi_sValueGap = sValuePart.Substring(iGapEnd + sValue.Length);
        }

        /* The entry; every character of the line is now in one of its   */
        /* fields (key, gap, value, trailing spaces), in the order the   */
        /* write puts them back.                                         */
        return(item);
    }

    /*------------------------------------------------------------------------*/
    /* Vin_LeadingSpaces:                                                     */
    /*                                                                        */
    /* The spaces and tabs a text starts with: the gap Vin_ParseLine keeps    */
    /* apart before a value and before a section line's comment.  The same    */
    /* two characters Vin_TrimSpaces removes, so what one counts off the      */
    /* other would have trimmed.                                              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     s : the text.                                                      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the leading run (possibly "").                            */
    /*------------------------------------------------------------------------*/
    static string Vin_LeadingSpaces(string s)
    {
        int i;

        /* From the start, past every space and tab; the length test comes    */
        /* first, so an all-space text stops at its end rather than reading   */
        /* past it.                                                           */
        i = 0;
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t'))
        {   /* One space or tab per iteration. */
            i++;
        }

        /* The run up to the first other character -- the whole text when   */
        /* it is all spaces, "" when it starts with something else.         */
        return(s.Substring(0, i));
    }

    /*------------------------------------------------------------------------*/
    /* Vin_TrimSpaces:                                                        */
    /*                                                                        */
    /* A text without the spaces and tabs at either end -- only those, as the */
    /* file header's rules trim nothing else.  Used both to read a line and   */
    /* to judge a typed key or value (Vin_KeyProblem, Vin_ValueProblem), so   */
    /* what is refused when typed is exactly what reading would strip.        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     s : the text.                                                      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the trimmed text.                                         */
    /*------------------------------------------------------------------------*/
    static string Vin_TrimSpaces(string s)
    {
        /* Spaces and tabs only, named, rather than Trim() with no argument,   */
        /* which trims every white-space character (as found -- the            */
        /* framework's source is not in the tree).  Vin_ParseLine relies on    */
        /* this trimming exactly what Vin_LeadingSpaces counts: it takes       */
        /* the trimmed key as the start of the key part, and the value as      */
        /* starting at iGapEnd.  A wider trim would break both, and the        */
        /* line would not write back as it was read.                           */
        return(s.Trim(new char[] { ' ', '\t' }));
    }

    /*------------------------------------------------------------------------*/
    /* Vin_WriteEntry:                                                        */
    /*                                                                        */
    /* An entry's line, the reverse of Vin_ParseLine: the stored pieces put   */
    /* back in the order they were taken, so a line read and not edited is    */
    /* written byte for byte, and a new one is written in the file's style.   */
    /* Its one caller is the line engine (VoorheesLines.cs,                   */
    /* Vln_WriteEntryLine), which writes the lead before it.                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc  : the document (its delimiter style).                         */
    /*     item : the entry.                                                  */
    /*     sb   : where the line goes.                                        */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the line is appended (no line break: leads hold those).     */
    /*------------------------------------------------------------------------*/
    public static void Vin_WriteEntry(VoorheesDocument doc, VoorheesItem item,
        StringBuilder sb)
    {
        /* The key, as read or as the user named it. */
        sb.Append(item.Vi_sKey);

        if (item.Vi_node.Vn_iKind != VIN_KEYONLY)
        {   /* An entry with a value (VIN_TEXT): the gap with its "=", then   */
            /* the value's text.  A key on its own writes neither -- not      */
            /* even a gap it may still carry from when it had a value --      */
            /* or it would read back as an entry with an empty value.         */
            if (item.Vi_sKeyGap != null)
            {   /* Read from the file: the line's own spaces and "=" back   */
                /* exactly.                                                 */
                sb.Append(item.Vi_sKeyGap);
            }
            else
            {   /* No stored gap (a new entry, a pasted one, or a key on    */
                /* its own that has been given a value): the document's     */
                /* delimiter -- the gap its entries use most                */
                /* (Vin_LoadDocument), or "=" for a new file.  Either way   */
                /* it holds an "=", so the line reads back as an entry.     */
                sb.Append(doc.Vd_sDelimiter);
            }
            sb.Append(item.Vi_node.Vn_sText);
        }

        if (item.Vi_sValueGap != null)
        {   /* Trailing spaces or tabs, as read; a new entry has none. */
            sb.Append(item.Vi_sValueGap);
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vin_WriteHeader:                                                       */
    /*                                                                        */
    /* A section's header line (not the lines under it), the reverse of the   */
    /* section half of Vin_ParseLine, so a header read and not edited is      */
    /* written byte for byte.  Its one caller is the line engine              */
    /* (VoorheesLines.cs, Vln_WriteHeaderLine).                               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     item : the section.                                                */
    /*     sb   : where the line goes.                                        */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the line is appended.                                       */
    /*------------------------------------------------------------------------*/
    public static void Vin_WriteHeader(VoorheesItem item, StringBuilder sb)
    {
        /* [name].  The name cannot hold a "]" that would end it early:       */
        /* reading takes it up to the first one, and Vin_KeyProblem refuses   */
        /* one in a typed name.                                               */
        sb.Append('[');
        sb.Append(item.Vi_sKey);
        sb.Append(']');

        if (item.Vi_sOpenComment != null)
        {   /* A comment on the header line.  It wins over the trailing    */
            /* spaces: reading never sets both, and a comment added to a   */
            /* header that had only spaces after it drops them.            */
            if (item.Vi_sOpenGap != null)
            {   /* Read from the file: its own spacing back, possibly "". */
                sb.Append(item.Vi_sOpenGap);
            }
            else
            {   /* A new comment: one space between the "]" and it. */
                sb.Append(' ');
            }
            sb.Append(item.Vi_sOpenComment);
        }
        else if (item.Vi_sValueGap != null)
        {   /* No comment, but spaces or tabs after the "]": back as read. */
            sb.Append(item.Vi_sValueGap);
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vin_LoadDocument:                                                      */
    /*                                                                        */
    /* A whole file's bytes to an INI document: decoded, read by the line     */
    /* engine, and its style taken from the file so that what the user adds   */
    /* looks like what is already there.  Called by the dispatcher            */
    /* (VoorheesFormat.cs, Vf_Load) for an INI file.                          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     bytes  : the file's contents.                                      */
    /*     sPath  : the file, for the document to remember; may be null.      */
    /*     sError : set to null (an INI file always reads).                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesDocument : the document.                                   */
    /*------------------------------------------------------------------------*/
    public static VoorheesDocument Vin_LoadDocument(byte[] bytes, string sPath,
        out string sError)
    {
        Encoding encoding;                               // the file's encoding
        bool bBom;                                       // it had a byte order mark
        string sText;                                    // the decoded file
        VoorheesNode root;                               // the document node
        VoorheesDocument doc;                            // the document

        /* The bytes as text, with the encoding and mark noted so the save    */
        /* writes them back the same way.  A marked file whose bytes do not   */
        /* decode is refused here (VoorheesText.cs).  The line engine then    */
        /* reads every line by Vin_ParseLine; for INI it never refuses (a     */
        /* line that is nothing else is a key on its own), so sError comes    */
        /* back null and root is always a document node.                      */
        sText = VoorheesText.Vtx_Decode(bytes, out encoding, out bBom, out sError);
        if (sText == null)
        {   /* Refused by the decoder: sError says why. */
            return(null);
        }

        root = VoorheesLines.Vln_Parse(VoorheesFormat.VF_INI, sText, null,
            out sError);

        /* The document around it, with the file's own line ending (null   */
        /* for a file with no line break, which then takes the default,    */
        /* CRLF).  The three nulls after it are JSON's indentation and     */
        /* key / colon gaps, which INI does not use.                       */
        doc = VoorheesDocument.Vd_Create(VoorheesFormat.VF_INI, root, sPath,
            encoding, bBom, VoorheesText.Vtx_DetectNewline(sText), null, null,
            null);
        /* The style for new entries and comments, from the file: the gap   */
        /* round "=" its entries use most, and the comment marker its       */
        /* comments use most; a file with no entry or no comment keeps      */
        /* INI's default ("=" or ";").  Done after Vd_Create, which first   */
        /* sets those defaults (Vf_ApplyDefaultStyle).                      */
        doc.Vd_sDelimiter = VoorheesLines.Vln_DetectDelimiter(root,
            VIN_DEFAULTDELIMITER);
        doc.Vd_sCommentPrefix = VoorheesLines.Vln_DetectCommentPrefix(root,
            VIN_DEFAULTCOMMENTPREFIX);

        /* The document, unmodified, with its encoding and style. */
        return(doc);
    }

    /*------------------------------------------------------------------------*/
    /* Vin_SaveBytes:                                                         */
    /*                                                                        */
    /* An INI document to the bytes of its file, in its own encoding.         */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc : the document.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     byte[] : the file's new contents.                                  */
    /*------------------------------------------------------------------------*/
    public static byte[] Vin_SaveBytes(VoorheesDocument doc)
    {
        /* The text by the line engine (which calls back into this file for   */
        /* each entry and header line), then encoded in the encoding and      */
        /* byte-order mark the file was read with, so an unedited file        */
        /* saves as it was.  Called by the dispatcher (VoorheesFormat.cs,     */
        /* Vf_SaveBytes).                                                     */
        return(VoorheesText.Vtx_Encode(VoorheesLines.Vln_Write(doc),
            doc.Vd_encoding, doc.Vd_bBom));
    }

    /*------------------------------------------------------------------------*/
    /* Vin_NewDocument:                                                       */
    /*                                                                        */
    /* A new, empty INI document: UTF-8 without a BOM, CRLF, "key=value",     */
    /* ";" comments.  Reached when the main window opens the editor on a new  */
    /* INI file (JsonEditForm.cs, its OnLoad, through                         */
    /* VoorheesDocument.Vd_CreateNew and VoorheesFormat.Vf_NewDocument).      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     None.                                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesDocument : the document.                                   */
    /*------------------------------------------------------------------------*/
    public static VoorheesDocument Vin_NewDocument()
    {
        VoorheesNode root;                               // the empty document node

        /* The document node with no items: an empty file. */
        root = VoorheesNode.Vn_NewContainer(VoorheesNode.VN_DOCUMENT);

        /* No path, encoding, line ending or JSON spacing given (the nulls   */
        /* and false), so Vd_Create takes every default: UTF-8 without a     */
        /* mark and CRLF, and INI's "=" and ";" (Vf_ApplyDefaultStyle).      */
        /* Those are the values this header promises.                        */
        return(VoorheesDocument.Vd_Create(VoorheesFormat.VF_INI, root, null,
            null, false, null, null, null, null));
    }

    /*------------------------------------------------------------------------*/
    /* Vin_KindName:                                                          */
    /*                                                                        */
    /* An INI kind's name, for the Type box, the Add Child menu and messages  */
    /* (JsonEditForm.cs, through VoorheesFormat.cs Vf_KindName; the messages  */
    /* use it lower-cased).                                                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iKind : a VIN_ kind.                                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the name; "Value" for anything else.                      */
    /*------------------------------------------------------------------------*/
    public static string Vin_KindName(int iKind)
    {
        switch (iKind)
        {   /* One name per INI kind.              */
            /* An entry with a value: key=value.   */
            case VIN_TEXT:    return("Text");
            /* A key on a line of its own, no "=". */
            case VIN_KEYONLY: return("Key Only");
            /* A [name] header and the lines under it. */
            case VIN_SECTION: return("Section");
            /* Not an INI kind: the dispatcher sends only INI's own kinds   */
            /* for an INI document, so this is a guard, answering a plain   */
            /* word rather than failing.                                    */
            default:          return("Value");
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vin_ChildKinds:                                                        */
    /*                                                                        */
    /* What Add Child offers in a container: entries (with a value or a key   */
    /* alone) anywhere, and sections only at the document level -- an INI     */
    /* file has no nesting, and Vin_FitItem refuses a section anywhere else.  */
    /* The editor's Add menus are built from this list (JsonEditForm.cs,      */
    /* through VoorheesFormat.cs Vf_ChildKinds).                              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     container : the document node or a section.                        */
    /*                                                                        */
    /* Returns:                                                               */
    /*     List<int> : the kinds, in menu order (a new list).                 */
    /*------------------------------------------------------------------------*/
    public static List<int> Vin_ChildKinds(VoorheesNode container)
    {
        List<int> lstKinds;                              // the kinds

        /* The two kinds of entry, offered in every container, with a value   */
        /* first.  A new list each time, so a caller that changes it          */
        /* changes nothing here.                                              */
        lstKinds = new List<int>();
        lstKinds.Add(VIN_TEXT);
        lstKinds.Add(VIN_KEYONLY);
        if (container.Vn_iKind == VoorheesNode.VN_DOCUMENT)
        {   /* The top level: sections too, the only place one can go. */
            lstKinds.Add(VIN_SECTION);
        }

        /* Entries first, then (at the top) sections: the menu's order. */
        return(lstKinds);
    }

    /*------------------------------------------------------------------------*/
    /* Vin_PanelKinds:                                                        */
    /*                                                                        */
    /* What the Type box offers for an entry: a section stays a section (its  */
    /* lines would have nowhere to go); an entry can switch between having a  */
    /* value and being a key alone.  Vin_ValueFromPanel relies on this list:  */
    /* it treats the other choices as unreachable for a section.              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     node : the entry's value.                                          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     List<int> : the kinds (a new list).                                */
    /*------------------------------------------------------------------------*/
    public static List<int> Vin_PanelKinds(VoorheesNode node)
    {
        List<int> lstKinds;                              // the kinds

        /* A new list each time, filled by what the value is. */
        lstKinds = new List<int>();
        if (node.Vn_iKind == VIN_SECTION)
        {   /* A section: only that, so the Type box cannot turn it into   */
            /* an entry and drop the lines under it.                       */
            lstKinds.Add(VIN_SECTION);
            return(lstKinds);
        }

        /* An entry: its two kinds, either way round -- giving a key alone   */
        /* a value, or taking an entry's value away.                         */
        lstKinds.Add(VIN_TEXT);
        lstKinds.Add(VIN_KEYONLY);

        /* An entry's list: with a value first, then the key alone. */
        return(lstKinds);
    }

    /*------------------------------------------------------------------------*/
    /* Vin_ValueProblem:                                                      */
    /*                                                                        */
    /* Why text cannot be an INI value, if it cannot: a value that would not  */
    /* read back as itself once written.  Used for every value that enters a  */
    /* document -- typed in the panel (Vin_ValueFromPanel), edited in place   */
    /* or replaced (Vin_ConvertText), or brought from another format          */
    /* (VoorheesFormat.cs, its import).                                       */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sText : the value.                                                 */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the reason, or null when it is a valid value.             */
    /*------------------------------------------------------------------------*/
    public static string Vin_ValueProblem(string sText)
    {
        if (sText.IndexOf('\r') >= 0 || sText.IndexOf('\n') >= 0)
        {   /* Several lines: a line break would end the entry, and the   */
            /* rest would read back as lines of their own.                */
            return("An INI value is one line.");
        }

        if (Vin_TrimSpaces(sText).Length != sText.Length)
        {   /* Spaces or tabs at an end: Vin_ParseLine trims them off a    */
            /* value it reads, so they would be lost on the next open --   */
            /* Windows does the same (as found).  The message's way out:   */
            /* in quotes the spaces are no longer at the ends, and this    */
            /* editor keeps the quotes as part of the value.               */
            return("Windows strips spaces at the start and end of an INI "
                + "value; put the value in quotes to keep them.");
        }

        /* A valid value.  Nothing else in the text matters: INI has no     */
        /* inline comments, so a ";" is kept, and only the FIRST "=" on a   */
        /* line splits it, so an "=" in the value is kept too.              */
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* Vin_ValueFromPanel:                                                    */
    /*                                                                        */
    /* The value the edit panel describes when Save is pressed: Text from the */
    /* Value box (one line, no spaces at the ends), Key Only (no value), or   */
    /* the section itself.  Called through VoorheesFormat.cs                  */
    /* (Vf_ValueFromPanel) by the panel's apply (JsonEditForm.cs).            */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iKind   : the VIN_ kind chosen.                                    */
    /*     sText   : the Value box's text.                                    */
    /*     oldNode : the value being replaced.                                */
    /*     sError  : set to why the value is refused, else null.              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNode : the value, or null when refused.                    */
    /*------------------------------------------------------------------------*/
    public static VoorheesNode Vin_ValueFromPanel(int iKind, string sText,
        VoorheesNode oldNode, out string sError)
    {
        /* No objection until one is found. */
        sError = null;
        if (iKind == VIN_SECTION)
        {   /* Section chosen.  Vin_PanelKinds offers it only for a section,   */
            /* and offers a section nothing else, so this is the section       */
            /* saved as it is.                                                 */
            if (oldNode.Vn_iKind == VIN_SECTION)
            {   /* Unchanged: the very node handed back, lines and all. */
                return(oldNode);
            }
            /* An entry asked to become a section -- unreachable through     */
            /* the Type box (an entry is not offered Section), so a guard:   */
            /* refused, since an entry's value cannot hold lines.            */
            sError = "An entry can't become a section; add a section at the "
                + "top level instead.";
            return(null);
        }

        if (iKind == VIN_KEYONLY)
        {   /* The key alone: a fresh VIN_KEYONLY value.  The Value box's    */
            /* text is not used -- whatever it holds is dropped -- and the   */
            /* entry's line will be written with no "=".                     */
            return(VoorheesNode.Vn_NewLeaf(VIN_KEYONLY, ""));
        }

        /* Text (VIN_TEXT): the box's text, held to the one rule a value   */
        /* has -- it must read back as itself (Vin_ValueProblem).          */
        sError = Vin_ValueProblem(sText);
        if (sError != null)
        {   /* Refused, with the reason in sError for the caller to show. */
            return(null);
        }

        /* The text exactly as typed (quotes and all) as a new value; the   */
        /* entry's stored gap and trailing spaces are the caller's.         */
        return(VoorheesNode.Vn_NewLeaf(VIN_TEXT, sText));
    }

    /*------------------------------------------------------------------------*/
    /* Vin_NewValue:                                                          */
    /*                                                                        */
    /* An empty value of a kind: "" text, a key alone, or an empty section.   */
    /* Used by Vin_NewChild for Add Child's new item (its other caller,       */
    /* VoorheesFormat.cs Vf_NewValue, is called by nothing in this program).  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iKind : a VIN_ kind.                                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNode : the value.                                          */
    /*------------------------------------------------------------------------*/
    public static VoorheesNode Vin_NewValue(int iKind)
    {
        VoorheesNode section;                            // a new section's container

        if (iKind == VIN_SECTION)
        {   /* An empty section: a container with no lines, its closing   */
            /* text stored as "" exactly as Vin_ParseLine stores a read   */
            /* one's -- an INI section has none.                          */
            section = VoorheesNode.Vn_NewContainer(VIN_SECTION);
            section.Vn_sClose = "";
            return(section);
        }

        if (iKind == VIN_KEYONLY)
        {   /* A key alone: written with no "=". */
            return(VoorheesNode.Vn_NewLeaf(VIN_KEYONLY, ""));
        }

        /* Anything else is taken as text: an entry with an empty value,   */
        /* written "key=".                                                 */
        return(VoorheesNode.Vn_NewLeaf(VIN_TEXT, ""));
    }

    /*------------------------------------------------------------------------*/
    /* Vin_ConvertText:                                                       */
    /*                                                                        */
    /* A value from edited text (in-place editing, Replace): text stays text  */
    /* if it is still a valid value; a key alone that is given text becomes   */
    /* key=text ("" leaves it alone); a section has no text.  Called through  */
    /* VoorheesFormat.cs (Vf_ConvertText) by the tree's in-place edit         */
    /* (JsonEditForm.cs) and by Replace (VoorheesDocument.cs).                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     oldNode : the value being replaced.                                */
    /*     sText   : the new text.                                            */
    /*     sReason : set to Vin_ValueProblem's reason when the text is        */
    /*               refused for it (several lines, spaces at an end); ""     */
    /*               otherwise.                                               */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNode : the new value, or null when refused.                */
    /*------------------------------------------------------------------------*/
    public static VoorheesNode Vin_ConvertText(VoorheesNode oldNode,
        string sText, out string sReason)
    {
        string sProblem;                                 // why the text would not read back as itself, or null

        /* No reason until a refusal has one. */
        sReason = "";

        if (oldNode.Vn_iKind == VIN_SECTION)
        {   /* A section has no text to edit: refused, with no reason of   */
            /* its own -- the in-place edit says the text is not a valid   */
            /* value of the kind and points to the Type box, and Replace   */
            /* counts the value as skipped.                                */
            return(null);
        }

        if (oldNode.Vn_iKind == VIN_KEYONLY && sText.Length == 0)
        {   /* A key alone left empty: still no value, so the very node     */
            /* comes back and the line keeps having no "=".  Without this   */
            /* test it would become "key=", an entry with an empty value.   */
            return(oldNode);
        }

        /* Whether the text would read back from the file as itself. */
        sProblem = Vin_ValueProblem(sText);
        if (sProblem != null)
        {   /* Not a value that would read back as itself (several lines,    */
            /* or spaces at an end): refused, with that reason, which the    */
            /* in-place edit shows -- the value's kind is not the trouble,   */
            /* so the Type box is no way out.                                */
            sReason = sProblem;
            return(null);
        }

        /* The text as a value.  A key alone given text gains its "=" this   */
        /* way: a VIN_TEXT value is written with the delimiter.              */
        return(VoorheesNode.Vn_NewLeaf(VIN_TEXT, sText));
    }

    /*------------------------------------------------------------------------*/
    /* Vin_NewChild:                                                          */
    /*                                                                        */
    /* Add Child's new item: a section named with the first free variant of   */
    /* "newSection", or an entry keyed with the first free variant of         */
    /* "newKey" (with "" text, or a key alone).                               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc       : the document (for free names).                         */
    /*     container : the document node or a section.                        */
    /*     iKind     : a VIN_ kind.                                           */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesItem : the new item.                                       */
    /*------------------------------------------------------------------------*/
    public static VoorheesItem Vin_NewChild(VoorheesDocument doc,
        VoorheesNode container, int iKind)
    {
        if (iKind == VIN_SECTION)
        {   /* A section (offered only at the top level, Vin_ChildKinds):     */
            /* "newSection", or "newSection1", "newSection2" ... -- the       */
            /* first not already in the container, names compared without     */
            /* case as INI compares them (Vd_UnusedKey), so the new section   */
            /* never reads as a repeat of an existing one.                    */
            return(VoorheesItem.Vi_NewMember(doc.Vd_UnusedKey(container,
                VIN_NEWSECTIONBASE), Vin_NewValue(VIN_SECTION)));
        }

        /* An entry, with a value or the key alone as iKind says, keyed    */
        /* "newKey" or its first free variant the same way -- at the top   */
        /* level too, where Vin_NewKeyBase would have said "newSection".   */
        return(VoorheesItem.Vi_NewMember(doc.Vd_UnusedKey(container,
            VIN_NEWKEYBASE), Vin_NewValue(iKind)));
    }

    /*------------------------------------------------------------------------*/
    /* Vin_NewKeyBase:                                                        */
    /*                                                                        */
    /* The name a new item in a container starts from: "newSection" at the    */
    /* top level (where sections go), else "newKey".  NOTHING IN THIS PROGRAM */
    /* CALLS IT: its one caller, VoorheesFormat.cs Vf_NewKeyBase, is itself   */
    /* called by nothing.  Add Child names its items in Vin_NewChild, which   */
    /* gives a top-level ENTRY "newKey" rather than this "newSection".        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     container : the container.                                         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the base name.                                            */
    /*------------------------------------------------------------------------*/
    public static string Vin_NewKeyBase(VoorheesNode container)
    {
        if (container.Vn_iKind == VoorheesNode.VN_DOCUMENT)
        {   /* The top level, the only place a section can go: a section's   */
            /* base name.                                                    */
            return(VIN_NEWSECTIONBASE);
        }

        /* Inside a section, where only entries go: a key's base name. */
        return(VIN_NEWKEYBASE);
    }

    /*------------------------------------------------------------------------*/
    /* Vin_FitItem:                                                           */
    /*                                                                        */
    /* Whether an item can go into an INI container: sections only at the     */
    /* top level; every entry needs a key (an unkeyed value -- from a JSON    */
    /* array, say -- has none).  Comments go anywhere.  Called through        */
    /* VoorheesFormat.cs both for an item about to be inserted (Vf_FitItem:   */
    /* every insert, and a paste once it has been converted to INI's kinds)   */
    /* and for an item whose value is about to change (Vf_CheckItem), so the  */
    /* rule holds however an item arrives.                                    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     container : where it goes.                                         */
    /*     item      : the item.                                              */
    /*     sError    : set to why it cannot go there, else null.              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesItem : the item, or null when refused.                     */
    /*------------------------------------------------------------------------*/
    public static VoorheesItem Vin_FitItem(VoorheesNode container,
        VoorheesItem item, out string sError)
    {
        /* No objection until one is found. */
        sError = null;
        if (item.Vi_iKind == VoorheesItem.VI_ELEMENT)
        {   /* An element -- a value with no key, as in a JSON array: an   */
            /* INI line needs a key, so it cannot be written.  Refused.    */
            sError = "INI entries need a key.";
            return(null);
        }

        /* Vi_IsValue first: a comment has no value node to look at. */
        if (item.Vi_IsValue() && item.Vi_node.Vn_iKind == VIN_SECTION
            && container.Vn_iKind != VoorheesNode.VN_DOCUMENT)
        {   /* A section inside a section: INI has no nesting -- its header   */
            /* line would simply end the outer section -- so refused.         */
            sError = "A section can only be at the top level of an INI file.";
            return(null);
        }

        /* It fits, unchanged: INI never alters an item to make it fit, it   */
        /* only refuses.                                                     */
        return(item);
    }

    /*------------------------------------------------------------------------*/
    /* Vin_KeyProblem:                                                        */
    /*                                                                        */
    /* Why a new key or section name would not read back as itself:           */
    /*     o a section name: no "]" (the name ends at the first one) and no   */
    /*       line break;                                                      */
    /*     o a key: not empty; no "=" (the first one ends the key), no line   */
    /*       break; not starting with ";", "#" or "[" (the line would read as */
    /*       a comment or a section); no spaces at its ends (Vin_ParseLine    */
    /*       trims them, as Windows does -- as found).                        */
    /* A repeat is not looked for here: VoorheesDocument.cs refuses a new one */
    /* itself, right after this check, on insert and on rename (names         */
    /* compared without case).  Called through VoorheesFormat.cs              */
    /* (Vf_KeyProblem) from those two places.                                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     item    : the entry or section (its value says which).             */
    /*     sNewKey : the proposed key or name.                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the reason, or null when it is acceptable.                */
    /*------------------------------------------------------------------------*/
    public static string Vin_KeyProblem(VoorheesItem item, string sNewKey)
    {
        if (sNewKey.IndexOf('\r') >= 0 || sNewKey.IndexOf('\n') >= 0)
        {   /* A line break, in a name or a key: the line would end there   */
            /* and the rest read back as a line of its own.                 */
            return("A key or section name is one line.");
        }

        if (item.Vi_node.Vn_iKind == VIN_SECTION)
        {   /* A section name: only a "]" can stop it reading back, since   */
            /* everything between "[" and the first "]" is the name --      */
            /* spaces, a leading ";" and an empty name all read back.       */
            if (sNewKey.IndexOf(']') >= 0)
            {   /* A "]" would end the name early, and the rest would read   */
                /* as the header line's comment.                             */
                return("A section name can't contain \"]\".");
            }
            return(null);
        }

        if (sNewKey.Length == 0)
        {   /* Empty key: refused, though a line starting "=" does read     */
            /* with an empty key (Vin_ParseLine); why one may be read but   */
            /* not typed is not recorded.                                   */
            return("The key can't be empty.");
        }

        if (sNewKey.IndexOf('=') >= 0)
        {   /* An "=" would end the key early: the first one on the line   */
            /* splits key from value.                                      */
            return("An INI key can't contain \"=\".");
        }

        if (sNewKey[0] == ';' || sNewKey[0] == '#' || sNewKey[0] == '[')
        {   /* The line would read as a comment, or as a section.  A "["    */
            /* is refused even with no "]" in the key: the value can hold   */
            /* one ("[a" with the value "b]" writes "[a=b]", a section).    */
            return("An INI key can't start with \";\", \"#\" or \"[\".");
        }

        if (Vin_TrimSpaces(sNewKey).Length != sNewKey.Length)
        {   /* Spaces or tabs at an end: reading trims them off a key, so   */
            /* they would be lost.                                          */
            return("Windows strips spaces at the start and end of a key.");
        }

        /* Acceptable: the line reads back with this key. */
        return(null);
    }

    /*------------------------------------------------------------------------*/
    /* Vin_CommentText:                                                       */
    /*                                                                        */
    /* A comment's words: without its ";" or "#" and one space after it.  A   */
    /* section line's comment that does not start with a marker is shown as   */
    /* it is.  The words are what the editor shows and what the user edits;   */
    /* Vin_MakeComment puts back exactly the marker and space taken off here, */
    /* so "; words" survives an edit that changes nothing.                    */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sRaw : the comment as stored (or null).                            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the words; "" for null.                                   */
    /*------------------------------------------------------------------------*/
    public static string Vin_CommentText(string sRaw)
    {
        if (sRaw == null)
        {   /* No comment (a section header with nothing after its "]"):   */
            /* no words.                                                   */
            return("");
        }

        if (sRaw.Length > 0 && (sRaw[0] == ';' || sRaw[0] == '#'))
        {   /* A marker -- always so for a comment line (Vin_ParseLine   */
            /* reads only those as comments): the words after it.        */
            if (sRaw.Length > 1 && sRaw[1] == ' ')
            {   /* "; words": ONE space taken off as well; a second space   */
                /* is kept as part of the words, so it is not lost when     */
                /* the comment is made again.                               */
                return(sRaw.Substring(2));
            }
            /* ";words", or the marker alone: everything after the   */
            /* marker ("" for the marker alone).                     */
            return(sRaw.Substring(1));
        }

        /* Text after a section's "]" with no marker (Vin_ParseLine takes   */
        /* any such text as the header line's comment): as written.         */
        return(sRaw);
    }

    /*------------------------------------------------------------------------*/
    /* Vin_MakeComment:                                                       */
    /*                                                                        */
    /* A new comment from its words: the file's usual marker, a space and the */
    /* words (the marker alone for no words).  A comment is one line.  Used   */
    /* for a new comment entry and a new header-line comment (through         */
    /* VoorheesFormat.cs, Vf_MakeComment), and by Vin_RemakeComment.          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sPrefix : the document's comment marker (Vd_sCommentPrefix).       */
    /*     sText   : the words.                                               */
    /*     sError  : set to why they cannot be a comment, else null.          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the comment, or null when refused.                        */
    /*------------------------------------------------------------------------*/
    public static string Vin_MakeComment(string sPrefix, string sText,
        out string sError)
    {
        /* No objection until one is found. */
        sError = null;
        if (sText.IndexOf('\r') >= 0 || sText.IndexOf('\n') >= 0)
        {   /* Several lines: INI has no comment that runs on, so the   */
            /* second line would read as a line of its own (an entry,   */
            /* unless it happened to start with a marker).  Refused,    */
            /* saying how to get the same effect.                       */
            sError = "An INI comment is one line; add another comment entry "
                + "for more.";
            return(null);
        }

        if (sText.Length == 0)
        {   /* No words: the marker alone, with no space after it. */
            return(sPrefix);
        }

        /* Marker, one space, words -- the space Vin_CommentText takes   */
        /* off again.  Nothing in the words needs escaping: everything   */
        /* after the marker belongs to the comment.                      */
        return(sPrefix + " " + sText);
    }

    /*------------------------------------------------------------------------*/
    /* Vin_RemakeComment:                                                     */
    /*                                                                        */
    /* An existing comment with new words, in its own style: unchanged words  */
    /* give it back as written; otherwise its own marker (and whether a       */
    /* space followed it) with the new words; a section line's comment that   */
    /* had no marker gets the file's usual one.  Used for a comment entry's   */
    /* words, a header line's comment, and Replace in comments (all in        */
    /* VoorheesDocument.cs, through VoorheesFormat.cs Vf_RemakeComment).      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sPrefix : the document's comment marker.                           */
    /*     sOldRaw : the comment as stored (never null: every caller has a    */
    /*               comment in hand).                                        */
    /*     sText   : the new words.                                           */
    /*     sError  : set to why they cannot be a comment, else null.          */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the comment, or null when refused.                        */
    /*------------------------------------------------------------------------*/
    public static string Vin_RemakeComment(string sPrefix, string sOldRaw,
        string sText, out string sError)
    {
        if (sText == Vin_CommentText(sOldRaw))
        {   /* Same words: the comment exactly as written, so an edit that   */
            /* changes nothing changes no byte (two spaces after the         */
            /* marker, say, or trailing spaces, survive).                    */
            sError = null;
            return(sOldRaw);
        }

        /* A marker with NO space after it, and words to put there.  The      */
        /* marker alone (";") counts as "no space", so its new words follow   */
        /* it directly (";words"), where a new comment would be "; words"     */
        /* -- whether that was meant is not recorded.                         */
        if (sOldRaw.Length > 0 && (sOldRaw[0] == ';' || sOldRaw[0] == '#')
            && !(sOldRaw.Length > 1 && sOldRaw[1] == ' ') && sText.Length > 0)
        {   /* Keep that look: its own marker, then the words.  The one-line   */
            /* rule is checked here itself, since Vin_MakeComment (which       */
            /* would add the space) is not used on this path.                  */
            if (sText.IndexOf('\r') >= 0 || sText.IndexOf('\n') >= 0)
            {   /* Several lines: refused, as Vin_MakeComment refuses them. */
                sError = "An INI comment is one line; add another comment "
                    + "entry for more.";
                return(null);
            }
            sError = null;
            return(sOldRaw.Substring(0, 1) + sText);
        }

        if (sOldRaw.Length > 0 && (sOldRaw[0] == ';' || sOldRaw[0] == '#'))
        {   /* Its own marker, kept even where the file mostly uses the     */
            /* other one; then a space and the words (or the marker alone   */
            /* for no words), refused if several lines.                     */
            return(Vin_MakeComment(sOldRaw.Substring(0, 1), sText, out sError));
        }

        /* No marker before -- a header line's text after its "]" that     */
        /* started with something else: the file's usual marker, a space   */
        /* and the words, so the comment now reads as one.                 */
        return(Vin_MakeComment(sPrefix, sText, out sError));
    }

    /*------------------------------------------------------------------------*/
    /* Vin_ResetLayout:                                                       */
    /*                                                                        */
    /* Clears a copied item's layout -- lead, key gap, trailing spaces, the   */
    /* space before a header comment, and inside a section the same for its   */
    /* lines -- so it is written in the document's style (Paste).  Keys,      */
    /* values and comments stay.  ONLY for a copy not in a document: it       */
    /* changes the item in place, records no undo step, and on an item in a   */
    /* document would change what the file saves as behind the user's back.   */
    /* Its caller (VoorheesDocument.cs, the paste, through VoorheesFormat.cs  */
    /* Vf_ResetLayout) hands it a private deep copy.                          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     item : the copy.                                                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the layout fields are null.                                 */
    /*------------------------------------------------------------------------*/
    public static void Vin_ResetLayout(VoorheesItem item)
    {
        int i;

        /* The four layout fields INI items use, set to null = "generate":   */
        /* the line engine then gives the lead of a sibling of the same      */
        /* shape, and the writers the document's delimiter, no trailing      */
        /* spaces, and one space before a header comment.  The comment       */
        /* itself (Vi_sOpenComment, Vi_sComment) is content and stays.       */
        item.Vi_sLead = null;
        item.Vi_sKeyGap = null;
        item.Vi_sValueGap = null;
        item.Vi_sOpenGap = null;

        /* A comment has no node, and an entry's node has no item list, so   */
        /* only a section passes this test.                                  */
        if (item.Vi_node != null && item.Vi_node.Vn_lstItems != null)
        {   /* A section: no closing text ("" as a read section has --   */
            /* whatever closing text the copy brought from elsewhere     */
            /* goes), then the same reset for every line under it.       */
            item.Vi_node.Vn_sClose = "";
            for (i = 0; i < item.Vi_node.Vn_lstItems.Count; i++)
            {   /* One line under the section per iteration (an entry or   */
                /* a comment: sections do not nest, so the recursion is    */
                /* one level deep).                                        */
                Vin_ResetLayout(item.Vi_node.Vn_lstItems[i]);
            }
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vin_CountKeyOnly:                                                      */
    /*                                                                        */
    /* How many keys stand alone on a line (no "=", no value) in a container  */
    /* and its sections, for the dispatcher's validation warnings             */
    /* (VoorheesFormat.cs, Vf_ValidateWarnings).  NOTHING IN THIS PROGRAM     */
    /* CALLS that, so nothing reaches this function either.                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     container : the document node (or a section).                      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the count.                                                   */
    /*------------------------------------------------------------------------*/
    public static int Vin_CountKeyOnly(VoorheesNode container)
    {
        VoorheesItem item;                               // an item looked at
        int iCount;                                      // keys alone so far
        int i;

        /* Every item of the container in turn; Vn_Count is 0 for a node   */
        /* with no list, so a leaf passed in counts nothing.               */
        iCount = 0;
        for (i = 0; i < container.Vn_Count(); i++)
        {   /* One item per iteration: a comment, an entry or a section. */
            item = container.Vn_lstItems[i];
            if (!item.Vi_IsValue())
            {   /* A comment: no key to count. */
                continue;
            }

            if (item.Vi_node.Vn_IsContainer())
            {   /* A section: the keys alone under it, counted the same way   */
                /* (one level: sections do not nest).                         */
                iCount += Vin_CountKeyOnly(item.Vi_node);
            }
            else if (item.Vi_node.Vn_iKind == VIN_KEYONLY)
            {   /* A key alone: one more. */
                iCount++;
            }
        }

        /* Every key alone in the container and under its sections. */
        return(iCount);
    }

    /*------------------------------------------------------------------------*/
    /* Vin_OpenWarnings:                                                      */
    /*                                                                        */
    /* What the user should be told when an INI file opens: repeated keys or  */
    /* sections, which Windows resolves by reading the FIRST copy (as found), */
    /* while this editor keeps them all.  Called through VoorheesFormat.cs    */
    /* (Vf_OpenWarnings) by the editor as it shows a file it has read         */
    /* (JsonEditForm.cs).                                                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sFileName    : the file's name (no folder).                        */
    /*     iExtraCopies : repeated-name copies (Vd_ExtraCopies, compared      */
    /*                    without case).                                      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     List<string> : the messages (a new list; empty for none).          */
    /*------------------------------------------------------------------------*/
    public static List<string> Vin_OpenWarnings(string sFileName,
        int iExtraCopies)
    {
        List<string> lstWarnings;                        // the messages

        /* A new list, empty unless something below is worth saying. */
        lstWarnings = new List<string>();
        if (iExtraCopies > 0)
        {   /* Something repeats: one message saying how many copies, that   */
            /* names compare without case, what Windows reads, and that      */
            /* this editor keeps and saves every copy -- the "(duplicate)"   */
            /* mark it names is the tree's (VoorheesTreeView.cs) -- and      */
            /* that removing the copies is the user's to do.                 */
            lstWarnings.Add(sFileName + " repeats a section or a key ("
                + iExtraCopies.ToString(CultureInfo.InvariantCulture)
                + " extra cop" + (iExtraCopies == 1 ? "y" : "ies")
                + "; names are compared without case).\r\n\r\n"
                + "Windows reads the FIRST copy of a repeated key.  This editor "
                + "keeps every copy, marks them \"(duplicate)\" in the tree "
                + "and saves them all back.  Delete or rename the copies you "
                + "do not want.");
        }

        /* The one warning, or none: the caller shows whatever is here. */
        return(lstWarnings);
    }
}
