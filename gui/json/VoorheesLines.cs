/*----------------------------------------------------------------------------*/
/* VoorheesLines.cs                                                           */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/*----------------------------------------------------------------------------*/
/* The LINE ENGINE: everything the line-based formats share -- INI, the       */
/* Klipper / Moonraker / KlipperScreen configs and REG files.  Those files    */
/* are lines, each a comment, a section                                       */
/* header, an entry (key, delimiter, value) or blank; the engine splits the   */
/* text into lines with their line endings kept, builds the model from them,  */
/* writes it back, renders one entry for the Raw box and parses a Raw edit.   */
/* What a line MEANS is the format module's business (VoorheesIni.cs for      */
/* INI, VoorheesCfg.cs for the Klipper family, VoorheesReg.cs for REG): the   */
/* engine asks it through a switch on the format id, the same way             */
/* VoorheesFormat dispatches, with no interfaces or virtual calls.            */
/*                                                                            */
/* LOSSLESS, like the JSON reader.  How the text maps onto the model:         */
/*     o the document node (VN_DOCUMENT) holds the top-level entries, the     */
/*       sections and the comments; its Vn_sClose is the text after the last  */
/*       line's content (the final line break and any trailing blank lines);  */
/*       a REG file's header line is kept in its Vn_sRaw.                     */
/*     o Vi_sLead of every item is the line break(s) before it, any blank     */
/*       lines, and the item line's own indentation: the line break that      */
/*       ENDS a line belongs to the NEXT item's lead.  The first item in the  */
/*       file has the text before it (usually "").                            */
/*     o a comment item's Vi_sComment is its line from the first non-space    */
/*       character to the line break (trailing spaces included); one line     */
/*       per item.                                                            */
/*     o a section is a VI_MEMBER whose value is a container (Vn_bOneLine     */
/*       false, Vn_sClose ""); its entries and comments are its items.        */
/*     o an entry is a VI_MEMBER with a plain value; Vi_sKeyGap holds         */
/*       everything between key and value, delimiter included.                */
/* Writing every stored string back in order therefore gives the file byte    */
/* for byte; fields left null (new or edited items) are generated in the      */
/* document's style.                                                          */
/*                                                                            */
/* COMMENT ATTRIBUTION (where a comment shows in the tree; the bytes do not   */
/* depend on it): comments after a section's last entry stay in that          */
/* section, EXCEPT a block of comments that is separated from what comes      */
/* before it by a blank line and attached (no blank line) to the next         */
/* section header: that block describes the next section, so it moves to      */
/* the document level, just above that header.                                */
/*                                                                            */
/* Class:                                                                     */
/*     VoorheesLines (Vln_) : static functions only; nothing is kept between  */
/*                            calls, so any of them may run on a worker       */
/*                            thread.                                         */
/*----------------------------------------------------------------------------*/

/* The framework parts used: the item lists and the offset and tally        */
/* dictionaries (Collections.Generic), the invariant culture for line and   */
/* column numbers in messages (Globalization), and the StringBuilder the    */
/* writers build text in (Text).                                            */
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

/*----------------------------------------------------------------------------*/
/* VoorheesLines                                                              */
/*                                                                            */
/* Parse, write, render and Raw-edit for the line formats.                    */
/*----------------------------------------------------------------------------*/
static class VoorheesLines
{
    /*------------------------------------------------------------------------*/
    /* Vln_Parse:                                                             */
    /*                                                                        */
    /* Reads decoded text into a document node, line by line:                 */
    /*     1. a line is found with its line ending (CRLF, LF or a lone CR);   */
    /*     2. a blank line (spaces and tabs only) joins the next item's lead; */
    /*     3. any other line's indentation joins the lead too, and the rest   */
    /*        of it goes to the format (Vln_ParseLine), which makes the item; */
    /*     4. a section goes into the document, after the comments attached   */
    /*        to it have been moved out of the section before it              */
    /*        (Vln_AttributeComments); anything else goes into the current    */
    /*        section, or into the document before the first section;         */
    /*     5. what is left after the last line's content is the document's    */
    /*        closing text.                                                   */
    /* Nothing is dropped or changed, so writing the result gives the text    */
    /* back exactly.                                                          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat   : the VoorheesFormat.VF_ line format.                    */
    /*     sText     : the decoded text.                                      */
    /*     lstStarts : if not null, receives the offset in sText where each   */
    /*                 TOP-LEVEL item's content starts (after its lead), in   */
    /*                 item order, for messages about positions.              */
    /*     sError    : set to why the text is not valid in the format (with   */
    /*                 line and column), else null.  INI is never invalid.    */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNode : the document node, or null when refused.            */
    /*------------------------------------------------------------------------*/
    public static VoorheesNode Vln_Parse(int iFormat, string sText,
        List<int> lstStarts, out string sError)
    {
        /* A whole file: auto-saved blocks are looked for (true), and it is   */
        /* read as the document itself, with no container above it (null)     */
        /* -- which is also what makes a REG file's header line checked.      */
        /* Called by each line format's loader (VoorheesIni.cs,               */
        /* VoorheesCfg.cs, VoorheesReg.cs).                                   */
        return(Vln_ParseLines(iFormat, sText, lstStarts, true, null,
            out sError));
    }

    /*------------------------------------------------------------------------*/
    /* Vln_ParseLines:                                                        */
    /*                                                                        */
    /* Vln_Parse's work, line by line, with two format hooks besides the      */
    /* line itself: a line that starts an auto-saved block (Vln_ParseBlock:   */
    /* the Klipper family's SAVE_CONFIG / "#~#" block) makes one item of      */
    /* everything from there to the block's last line, placed like a section  */
    /* header; and every item is checked against the format's strict rules    */
    /* (Vln_LineProblem), a refusal naming the line and column.  Called by    */
    /* Vln_Parse (a file), Vln_ApplyRawEdit (the Raw box), and                */
    /* VoorheesCfg.cs for an auto-saved block's contents.                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat   : the VoorheesFormat.VF_ line format.                    */
    /*     sText     : the decoded text.                                      */
    /*     lstStarts : if not null, receives each TOP-LEVEL item's content    */
    /*                 offset, in item order.                                 */
    /*     bBlocks   : look for auto-saved blocks (false inside one: blocks   */
    /*                 do not nest).                                          */
    /*     context   : the container the text is read as if inside, for the   */
    /*                 strict rules only (a Raw edit of an option in a        */
    /*                 section: Vln_ApplyRawEdit); null for a whole file.     */
    /*                 The document node (a Raw edit at the top level) is     */
    /*                 judged like a whole file, but any non-null context     */
    /*                 skips the header-line check.  Items still go into the  */
    /*                 new document node.                                     */
    /*     sError    : set to why the text is not valid (with line and        */
    /*                 column), else null.                                    */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesNode : the document node, or null when refused.            */
    /*------------------------------------------------------------------------*/
    public static VoorheesNode Vln_ParseLines(int iFormat, string sText,
        List<int> lstStarts, bool bBlocks, VoorheesNode context,
        out string sError)
    {
        VoorheesNode root;                               // the document being built
        VoorheesNode section;                            // the section entries go into, or null before the first
        VoorheesNode under;                              // what the strict rules see a line as under (null: nothing)
        VoorheesItem item;                               // the item a line makes
        Dictionary<VoorheesItem, int> dictStarts;        // each item's content offset (items compare by reference)
        string sProblem;                                 // why the format refuses a line
        int iLeadStart;                                  // where the next item's lead starts
        int iLineStart;                                  // where the current line starts
        int iContentStart;                               // where its content starts (after the indentation)
        int iLineEnd;                                    // where its content ends (its line break or the end)
        int iNext;                                       // where the following line starts
        int i;

        /* The new document node (multi-line: Vn_NewContainer already         */
        /* leaves Vn_bOneLine false, and it is set again here; why is not     */
        /* recorded).  No section is open yet, so lines go to the document.   */
        /* "under" is what the format's strict rules see a line as inside:    */
        /* nothing for a whole file, the edited item's container for a Raw    */
        /* edit -- the rules only, since the items still go into this new     */
        /* node.  Each item's content offset is kept by reference for the     */
        /* caller's position messages.                                        */
        sError = null;
        root = VoorheesNode.Vn_NewContainer(VoorheesNode.VN_DOCUMENT);
        root.Vn_bOneLine = false;
        section = null;
        under = null;
        if (context != null && context.Vn_iKind != VoorheesNode.VN_DOCUMENT)
        {   /* Read as if inside that container (a section, include or   */
            /* block); the document node as context means "top level",   */
            /* which is the same as none.                                */
            under = context;
        }
        dictStarts = new Dictionary<VoorheesItem, int>();

        /* The first item's lead starts at the very beginning of the text,   */
        /* and so does the first line.                                       */
        iLeadStart = 0;
        iLineStart = 0;
        if (context == null && Vln_HasHeader(iFormat))
        {   /* A whole file of a format with a header line (REG): the first   */
            /* line is it, checked before anything else is read.  A Raw       */
            /* edit (context set) holds no header and skips this.             */
            iLineEnd = iLineStart;
            while (iLineEnd < sText.Length && sText[iLineEnd] != '\r'
                && sText[iLineEnd] != '\n')
            {   /* One character of the first line per iteration, up to   */
                /* its line break or the end of the text.                 */
                iLineEnd++;
            }
            /* That line, without its break, judged by the format. */
            sProblem = Vln_HeaderProblem(iFormat, sText.Substring(0, iLineEnd));
            if (sProblem != null)
            {   /* Not this format's file: refused at line 1, column 1,   */
                /* with the format's reason; nothing has been built       */
                /* that needs undoing.                                    */
                sError = Vln_Position(sText, 0) + ": " + sProblem + ".";
                return(null);
            }

            /* Kept in the document node's Vn_sRaw: Vln_Write writes it     */
            /* first, and a document node with it is what Vln_CloseOf and   */
            /* Vln_GeneratedLead take for REG's.  The line break ending     */
            /* it starts the first item's lead; reading goes on at the      */
            /* line after it.                                               */
            root.Vn_sRaw = sText.Substring(0, iLineEnd);
            iLeadStart = iLineEnd;
            iLineStart = Vln_LineAfter(sText, iLineEnd);
        }

        while (iLineStart < sText.Length)
        {   /* One line per iteration -- or, when one starts here, a whole   */
            /* auto-saved block, or an entry with its continuation lines.    */
            /* The line: its content runs to the first CR or LF.             */
            iLineEnd = iLineStart;
            while (iLineEnd < sText.Length && sText[iLineEnd] != '\r'
                && sText[iLineEnd] != '\n')
            {   /* One character of the line per iteration. */
                iLineEnd++;
            }

            /* Its line ending: CRLF, LF, a lone CR, or none at the end.     */
            /* All three are line breaks here, so a file mixing them reads   */
            /* line for line as written; iNext is where the next line        */
            /* starts.                                                       */
            iNext = iLineEnd;
            if (iNext < sText.Length && sText[iNext] == '\r')
            {   /* CR, and LF after it if there is one. */
                iNext++;
                if (iNext < sText.Length && sText[iNext] == '\n')
                {   /* CRLF: both characters are the one break. */
                    iNext++;
                }
            }
            else if (iNext < sText.Length)
            {   /* LF (the only other character that ends the content). */
                iNext++;
            }

            /* Its indentation: spaces and tabs, which go into the item's   */
            /* lead, not its content -- the format modules rely on a line   */
            /* reaching them starting at its first non-space character.     */
            iContentStart = iLineStart;
            while (iContentStart < iLineEnd && (sText[iContentStart] == ' '
                || sText[iContentStart] == '\t'))
            {   /* One space or tab per iteration. */
                iContentStart++;
            }

            if (iContentStart == iLineEnd)
            {   /* A blank line (nothing, or only spaces and tabs): no item.   */
                /* iLeadStart is not moved, so the line, its spaces and its    */
                /* break all become part of the next item's lead.              */
                iLineStart = iNext;
                continue;
            }

            /* A line with content: an auto-saved block starting here (not   */
            /* indented), or else one line the format makes an item of.      */
            item = null;
            if (bBlocks && iContentStart == iLineStart && Vln_HasBlocks(iFormat))
            {   /* The format has blocks and the line is not indented: does   */
                /* a clean one start here?  (Inside a block, bBlocks is       */
                /* false: blocks do not nest.)                                */
                item = Vln_ParseBlock(iFormat, sText, iLineStart, out i);
                if (item != null)
                {   /* It does: the whole block is one item, and the line   */
                    /* being read becomes its last line, so the loop goes   */
                    /* on after it.                                         */
                    iLineEnd = i;
                    iNext = Vln_LineAfter(sText, iLineEnd);
                }
            }
            if (item == null)
            {   /* No block: one line, its content only, for the format to   */
                /* make an item of.                                          */
                item = Vln_ParseLine(iFormat, sText.Substring(iContentStart,
                    iLineEnd - iContentStart));
            }
            /* Its lead: everything since the previous item's content -- the   */
            /* break ending that line, any blank lines, this line's            */
            /* indentation.  Its content offset is kept for the caller.        */
            item.Vi_sLead = sText.Substring(iLeadStart, iContentStart - iLeadStart);
            dictStarts[item] = iContentStart;

            if (Vln_HasContinuations(iFormat)
                && item.Vi_iKind == VoorheesItem.VI_MEMBER
                && !item.Vi_node.Vn_IsContainer())
            {   /* An entry in a format with multi-line values: its          */
                /* continuation lines join its value, and iLineEnd / iNext   */
                /* move past the last of them, so the loop does not read     */
                /* them again as lines of their own.  The option line's      */
                /* indentation is what a continuation must exceed.           */
                Vln_TakeContinuations(iFormat, item, sText,
                    iContentStart - iLineStart, ref iLineEnd, ref iNext);
            }

            /* Refused where the format's program refuses the file -- after   */
            /* the continuations, so the rules see the whole value.           */
            sProblem = Vln_LineProblem(iFormat, item, under);
            if (sProblem != null)
            {   /* Not valid: the whole read is refused, saying where (the   */
                /* line and column of the item's first character) and why.   */
                sError = Vln_Position(sText, iContentStart) + ": " + sProblem
                    + ".";
                return(null);
            }

            if (item.Vi_iKind == VoorheesItem.VI_MEMBER
                && item.Vi_node.Vn_IsContainer())
            {   /* A section header (or a block, which is placed the same       */
                /* way): it always goes into the document, and it becomes the   */
                /* section later lines go into and the strict rules' "under".   */
                if (section != null)
                {   /* A section before it: comments at its end that describe   */
                    /* the new header move out first (Vln_AttributeComments),   */
                    /* so they land in the document just above it.              */
                    Vln_AttributeComments(root, section, item);
                }
                root.Vn_lstItems.Add(item);
                section = item.Vi_node;
                under = item.Vi_node;
            }
            else if (section != null)
            {   /* An entry or comment after a header: it belongs to that   */
                /* section.                                                 */
                section.Vn_lstItems.Add(item);
            }
            else
            {   /* Before the first section: the document level (INI's   */
                /* "global" entries).                                    */
                root.Vn_lstItems.Add(item);
            }

            /* The line break ending this line (or the block, or the last   */
            /* continuation) starts the next lead, and reading goes on at   */
            /* the next line.                                               */
            iLeadStart = iLineEnd;
            iLineStart = iNext;
        }

        /* Whatever follows the last line's content closes the document.     */
        /* A completely empty file has no closing text to keep: it is left   */
        /* to be generated, so the first line added gets a line break        */
        /* after it, as in a new document (and an empty file still writes    */
        /* as nothing).                                                      */
        if (sText.Length > 0)
        {   /* The text after the last line, kept exactly. */
            root.Vn_sClose = sText.Substring(iLeadStart);
        }

        if (lstStarts != null)
        {   /* The caller wants the top-level items' offsets, in item order    */
            /* (comments moved up by attribution included).  Only now, when    */
            /* the top level is final: attribution moves items into it as      */
            /* each header arrives, so the order is not known while reading.   */
            for (i = 0; i < root.Vn_Count(); i++)
            {   /* One top-level item per iteration: its recorded offset. */
                lstStarts.Add(dictStarts[root.Vn_lstItems[i]]);
            }
        }

        /* The whole text, item by item; writing it back gives the text. */
        return(root);
    }

    /*------------------------------------------------------------------------*/
    /* Vln_HasContinuations:                                                  */
    /*                                                                        */
    /* Whether a line format's values may run over several lines: the         */
    /* Klipper family's do (Python configparser: a line indented deeper than  */
    /* its option line continues the value -- as found, configparser's        */
    /* source is not in the tree), and so do REG's hex values, wrapped by     */
    /* ending a line with "\" (the REG module decides, Vrg_ContinuesAfter).   */
    /* INI's do not.                                                          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat : the VF_ line format.                                     */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true for Klipper, Moonraker, KlipperScreen and REG.         */
    /*------------------------------------------------------------------------*/
    static bool Vln_HasContinuations(int iFormat)
    {
        /* The configparser dialects, and REG's wrapped hex values: the four   */
        /* formats Vln_TakeContinuations and Vln_AddContinuation handle.       */
        return(iFormat == VoorheesFormat.VF_KLIPPER
            || iFormat == VoorheesFormat.VF_MOONRAKER
            || iFormat == VoorheesFormat.VF_KLIPPERSCREEN
            || iFormat == VoorheesFormat.VF_REG);
    }

    /*------------------------------------------------------------------------*/
    /* Vln_HasHeader:                                                         */
    /*                                                                        */
    /* Whether a line format's files start with a header line of their own,   */
    /* kept in the document node's Vn_sRaw and written first (REG's           */
    /* "Windows Registry Editor Version 5.00").                               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat : the VF_ line format.                                     */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true for REG.                                               */
    /*------------------------------------------------------------------------*/
    static bool Vln_HasHeader(int iFormat)
    {
        /* Only registry files: INI and the Klipper family start straight   */
        /* with their content.                                              */
        return(iFormat == VoorheesFormat.VF_REG);
    }

    /*------------------------------------------------------------------------*/
    /* Vln_HeaderProblem:                                                     */
    /*                                                                        */
    /* Why a file's first line is not its format's header, if it is not.      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat : the VF_ line format (one with a header).                 */
    /*     sLine   : the first line, without its line break.                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the reason, or null for a header.                         */
    /*------------------------------------------------------------------------*/
    static string Vln_HeaderProblem(int iFormat, string sLine)
    {
        switch (iFormat)
        {   /* The format's own header rule. */
            case VoorheesFormat.VF_REG:
                /* REG: "Windows Registry Editor Version 5.00" or          */
                /* "REGEDIT4", trailing spaces allowed (the REG module).   */
                return(VoorheesReg.Vrg_HeaderProblem(sLine));

            default:
                /* No header (Vln_HasHeader is false, so this is not   */
                /* called for one): anything goes.                     */
                return(null);
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vln_HasBlocks:                                                         */
    /*                                                                        */
    /* Whether a line format has auto-saved blocks: Klipper's SAVE_CONFIG     */
    /* ("#*#") and KlipperScreen's ("#~#") block at the end of the file.      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat : the VF_ line format.                                     */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true for Klipper and KlipperScreen.                         */
    /*------------------------------------------------------------------------*/
    static bool Vln_HasBlocks(int iFormat)
    {
        /* The two programs that write a block into their own config. */
        return(iFormat == VoorheesFormat.VF_KLIPPER
            || iFormat == VoorheesFormat.VF_KLIPPERSCREEN);
    }

    /*------------------------------------------------------------------------*/
    /* Vln_ParseBlock:                                                        */
    /*                                                                        */
    /* The auto-saved block starting at a line, by its format, if a clean one */
    /* does (VoorheesCfg.Vcf_ParseBlock says what clean means).               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat    : the VF_ line format.                                  */
    /*     sText      : the whole text.                                       */
    /*     iLineStart : where the line starts.                                */
    /*     iBlockEnd  : set to where the block's last line's content ends.    */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesItem : the block's item, or null when none starts here.    */
    /*------------------------------------------------------------------------*/
    static VoorheesItem Vln_ParseBlock(int iFormat, string sText,
        int iLineStart, out int iBlockEnd)
    {
        switch (iFormat)
        {   /* The format's own block reader. */
            case VoorheesFormat.VF_KLIPPER:
            case VoorheesFormat.VF_KLIPPERSCREEN:
                /* The Klipper family's blocks: SAVE_CONFIG's "#*#" and   */
                /* KlipperScreen's "#~#" (VoorheesCfg.cs).                */
                return(VoorheesCfg.Vcf_ParseBlock(iFormat, sText, iLineStart,
                    out iBlockEnd));

            default:
                /* No blocks (Vln_HasBlocks keeps other formats away).     */
                /* iBlockEnd must still be set; -1, never read, since no   */
                /* item comes back.                                        */
                iBlockEnd = -1;
                return(null);
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vln_LineAfter:                                                         */
    /*                                                                        */
    /* Where the line after a line's content starts: past its CRLF, LF or     */
    /* lone CR (at the end of the text: the end).                             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sText       : the text.                                            */
    /*     iContentEnd : where the line's content ends.                       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the next line's start.                                       */
    /*------------------------------------------------------------------------*/
    static int Vln_LineAfter(string sText, int iContentEnd)
    {
        if (iContentEnd < sText.Length && sText[iContentEnd] == '\r'
            && iContentEnd + 1 < sText.Length && sText[iContentEnd + 1] == '\n')
        {   /* CRLF: both characters are the one break. */
            return(iContentEnd + 2);
        }

        if (iContentEnd < sText.Length)
        {   /* LF or a lone CR: content ends only at a CR or LF, so the   */
            /* character here is one of them.                             */
            return(iContentEnd + 1);
        }

        /* The end of the text: the last line has no break after it. */
        return(iContentEnd);
    }

    /*------------------------------------------------------------------------*/
    /* Vln_LineProblem:                                                       */
    /*                                                                        */
    /* Why a format refuses an item read from a line, if it does (INI never   */
    /* does; the Klipper family refuses what its programs refuse:             */
    /* VoorheesCfg.Vcf_LineProblem; REG what regedit would not take:          */
    /* VoorheesReg.Vrg_LineProblem).                                          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat : the VF_ line format.                                     */
    /*     item    : the item (continuation lines included).                  */
    /*     under   : the container of the section (or include, or block) it   */
    /*               falls under, or null before the first.                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the reason, or null when it is fine.                      */
    /*------------------------------------------------------------------------*/
    static string Vln_LineProblem(int iFormat, VoorheesItem item,
        VoorheesNode under)
    {
        switch (iFormat)
        {   /* The format's own strict rules. */
            case VoorheesFormat.VF_KLIPPER:
            case VoorheesFormat.VF_MOONRAKER:
            case VoorheesFormat.VF_KLIPPERSCREEN:
                /* The Klipper family: its programs' refusals. */
                return(VoorheesCfg.Vcf_LineProblem(iFormat, item, under));

            case VoorheesFormat.VF_REG:
                /* REG: what regedit would not take. */
                return(VoorheesReg.Vrg_LineProblem(item, under));

            default:
                /* INI reads anything: no line is refused (the file    */
                /* header's "INI is never invalid", VoorheesIni.cs).   */
                return(null);
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vln_TakeContinuations:                                                 */
    /*                                                                        */
    /* After an entry line, the lines that continue its value.  For the       */
    /* Klipper family, by Python configparser's rule (as found: its source is */
    /* not in the tree): a line indented                                      */
    /* DEEPER than the option line continues the value; blank lines and       */
    /* comment lines (first non-space "#" or ";") do not end it but belong to */
    /* it only when a continuation line follows them; any other line ends it. */
    /* For REG, a hex value's line that ends in "\" takes the next line,      */
    /* whatever it holds (the REG module decides, Vrg_ContinuesAfter).        */
    /* The text from the option line's end through the last continuation      */
    /* line (line breaks, blank and comment lines inside, indentation) goes   */
    /* to the format, which adds it to the value; the parser then carries on  */
    /* after that line, so comments and blank lines after the value are       */
    /* items and leads as usual.                                              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat  : the VF_ line format.                                    */
    /*     item     : the entry just made from the option line.               */
    /*     sText    : the whole text.                                         */
    /*     iIndent  : the option line's indentation, in characters.           */
    /*     iLineEnd : in: where the option line's content ends; out: where    */
    /*                the last continuation line's content ends.              */
    /*     iNext    : in: where the line after the option line starts; out:   */
    /*                where the line after the last continuation starts.      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the value holds its continuation lines, if any.             */
    /*------------------------------------------------------------------------*/
    static void Vln_TakeContinuations(int iFormat, VoorheesItem item,
        string sText, int iIndent, ref int iLineEnd, ref int iNext)
    {
        int iFirstEnd;                                   // where the option line's content ends
        int iScan;                                       // start of the line being looked at
        int iEnd;                                        // where its content ends
        int iAfter;                                      // where the line after it starts
        int iContent;                                    // its first non-space character
        int iLastStart;                                  // REG: start of the value's last line so far

        /* The option line's own end, kept: what the value gains is the   */
        /* text from here to wherever iLineEnd ends up.                   */
        iFirstEnd = iLineEnd;
        if (iFormat == VoorheesFormat.VF_REG)
        {   /* REG: a hex value's line ending in "\" goes on to the next   */
            /* line, whatever it holds -- indentation plays no part.       */
            iLastStart = 0;
            if (iLineEnd > 0)
            {   /* The value's line starts after the last line break before   */
                /* its end (0 when there is none: the file's first line).     */
                iLastStart = sText.LastIndexOfAny(new char[] { '\r', '\n' }, iLineEnd - 1) + 1;
            }
            while (iNext < sText.Length && iNext > iLineEnd && VoorheesReg.Vrg_ContinuesAfter(
                item, sText.Substring(iLastStart, iLineEnd - iLastStart)))
            {   /* One continuation line per iteration: the REG module said   */
                /* the value's last line goes on, a line break follows it,    */
                /* and text follows that.  The next line becomes the last.    */
                iLastStart = iNext;
                iEnd = iNext;
                while (iEnd < sText.Length && sText[iEnd] != '\r' && sText[iEnd] != '\n')
                {   /* One character of the line per iteration. */
                    iEnd++;
                }
                iLineEnd = iEnd;
                iNext = Vln_LineAfter(sText, iEnd);
            }
            if (iLineEnd > iFirstEnd)
            {   /* Continuation lines: REG adds them to the value. */
                Vln_AddContinuation(iFormat, item, sText.Substring(iFirstEnd,
                    iLineEnd - iFirstEnd));
            }
            /* REG done: the rest of this function is configparser's rule. */
            return;
        }

        /* The Klipper family: scan the lines after the option line.  Only    */
        /* a continuation line moves iLineEnd / iNext, so blank and comment   */
        /* lines after the last one stay outside the value.                   */
        iScan = iNext;
        while (iScan < sText.Length)
        {   /* One line after the option line per iteration.      */
            /* The line: content end, line ending, indentation.   */
            iEnd = iScan;
            while (iEnd < sText.Length && sText[iEnd] != '\r' && sText[iEnd] != '\n')
            {   /* One character of the line per iteration. */
                iEnd++;
            }
            iAfter = iEnd;
            if (iAfter < sText.Length && sText[iAfter] == '\r')
            {   /* CR, and LF after it if there is one. */
                iAfter++;
                if (iAfter < sText.Length && sText[iAfter] == '\n')
                {   /* CRLF: both characters are the one break. */
                    iAfter++;
                }
            }
            else if (iAfter < sText.Length)
            {   /* LF. */
                iAfter++;
            }
            iContent = iScan;
            while (iContent < iEnd && (sText[iContent] == ' ' || sText[iContent] == '\t'))
            {   /* One space or tab of indentation per iteration. */
                iContent++;
            }

            if (iContent == iEnd || sText[iContent] == '#' || sText[iContent] == ';')
            {   /* Blank or a comment, whatever its indentation: it neither   */
                /* ends the value nor joins it yet -- it joins only if a      */
                /* continuation line comes after it.                          */
                iScan = iAfter;
                continue;
            }

            if (iContent - iScan <= iIndent)
            {   /* Not indented deeper than the option line (counted in   */
                /* characters, a tab as one): the value has ended.        */
                break;
            }

            /* A continuation line: the value now runs to its end, taking   */
            /* any blank and comment lines skipped before it.               */
            iLineEnd = iEnd;
            iNext = iAfter;
            iScan = iAfter;
        }

        if (iLineEnd > iFirstEnd)
        {   /* Continuation lines: the format adds them to the value. */
            Vln_AddContinuation(iFormat, item, sText.Substring(iFirstEnd,
                iLineEnd - iFirstEnd));
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vln_AddContinuation:                                                   */
    /*                                                                        */
    /* Hands an entry's continuation text to its format, which folds it into  */
    /* the value (Vln_TakeContinuations' last step).                          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat : the VF_ line format.                                     */
    /*     item    : the entry.                                               */
    /*     sMore   : the text from the option line's end through the last     */
    /*               continuation line's content (it starts with a line       */
    /*               break).                                                  */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the value includes it.                                      */
    /*------------------------------------------------------------------------*/
    static void Vln_AddContinuation(int iFormat, VoorheesItem item,
        string sMore)
    {
        switch (iFormat)
        {   /* The format that owns the value folds the text in. */
            case VoorheesFormat.VF_KLIPPER:
            case VoorheesFormat.VF_MOONRAKER:
            case VoorheesFormat.VF_KLIPPERSCREEN:
                /* The Klipper family: multi-line values (G-code macros). */
                VoorheesCfg.Vcf_AddContinuation(item, sMore);
                break;

            case VoorheesFormat.VF_REG:
                /* REG: a wrapped hex value. */
                VoorheesReg.Vrg_AddContinuation(item, sMore);
                break;

            default:
                /* No multi-line values: never reached, as only the formats   */
                /* Vln_HasContinuations names get here.  It throws rather     */
                /* than drop the text without a word.                         */
                throw new InvalidOperationException(VoorheesFormat.Vf_Name(iFormat)
                    + " values are one line.");
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vln_AttributeComments:                                                 */
    /*                                                                        */
    /* Before a section header goes into the document: of the comments at     */
    /* the end of the previous section (after its last entry), the block that */
    /* is attached to the new header -- no blank line between any of them and */
    /* the header -- and separated from what comes before it by a blank line  */
    /* moves to the document level, so it shows above the header it           */
    /* describes.  A block not separated from the entries above it stays (it  */
    /* is about them), and so does everything when the header itself follows  */
    /* a blank line.  The bytes are unaffected: the moved comments are        */
    /* written at the same point either way.                                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     root    : the document node.                                       */
    /*     section : the previous section's container.                        */
    /*     header  : the new section's item (not yet in the document).        */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the block, if any, is now at the end of the document node.  */
    /*------------------------------------------------------------------------*/
    static void Vln_AttributeComments(VoorheesNode root, VoorheesNode section,
        VoorheesItem header)
    {
        List<VoorheesItem> lstItems;                     // the section's items
        int iFirstTrailing;                              // first of the comments after the last entry
        int iBlock;                                      // first comment of the attached block
        int i;

        /* The previous section's items, worked on in place. */
        lstItems = section.Vn_lstItems;

        /* The comments after the section's last entry: back from the end   */
        /* while the item is a comment.  iFirstTrailing is then the first   */
        /* of them (Count when the section ends with an entry; 0 when it    */
        /* holds only comments).                                            */
        iFirstTrailing = lstItems.Count;
        while (iFirstTrailing > 0
            && lstItems[iFirstTrailing - 1].Vi_iKind == VoorheesItem.VI_COMMENT)
        {   /* One trailing comment per iteration. */
            iFirstTrailing--;
        }

        if (iFirstTrailing == lstItems.Count || Vln_HasBlankLine(header.Vi_sLead))
        {   /* No trailing comments, or a blank line between them and the   */
            /* header (it stands apart, so they are not its): nothing       */
            /* moves.                                                       */
            return;
        }

        /* The attached block: back from the last comment while no blank   */
        /* line separates a comment from the one after it.                 */
        iBlock = lstItems.Count - 1;
        while (iBlock > iFirstTrailing
            && !Vln_HasBlankLine(lstItems[iBlock].Vi_sLead))
        {   /* This comment follows the one before with no blank line:   */
            /* the block reaches back to that one too.                   */
            iBlock--;
        }

        if (!Vln_HasBlankLine(lstItems[iBlock].Vi_sLead))
        {   /* The block runs straight on from the entries above (no blank   */
            /* line anywhere back to them): it is theirs, and stays.         */
            return;
        }

        /* Move the block, in order, to the end of the document level --    */
        /* where the header is about to be added, so they sit just above    */
        /* it.  The previous section has no closing text of its own (a      */
        /* section's is "" -- an auto-saved block, which has one, runs to   */
        /* the end of the file, so no header follows it), so the written    */
        /* bytes come out in the same order either way.                     */
        for (i = iBlock; i < lstItems.Count; i++)
        {   /* One comment of the block per iteration. */
            root.Vn_lstItems.Add(lstItems[i]);
        }
        /* Then out of the section, all together, now they are copied. */
        lstItems.RemoveRange(iBlock, lstItems.Count - iBlock);
    }

    /*------------------------------------------------------------------------*/
    /* Vln_HasBlankLine:                                                      */
    /*                                                                        */
    /* Whether a lead holds a blank line: two or more line breaks (the first  */
    /* ends the line before; any more are blank lines).  The comment          */
    /* attribution's test (Vln_AttributeComments).  Public, though nothing    */
    /* outside this file calls it.                                            */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sLead : the lead, or null.                                         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when there is a blank line in it.                      */
    /*------------------------------------------------------------------------*/
    public static bool Vln_HasBlankLine(string sLead)
    {
        /* More than one line break: the first only ends the line before,   */
        /* so it takes a second for a line with nothing on it.  A null      */
        /* lead (a new item) counts none.                                   */
        return(Vln_CountLineBreaks(sLead) >= 2);
    }

    /*------------------------------------------------------------------------*/
    /* Vln_CountLineBreaks:                                                   */
    /*                                                                        */
    /* How many line breaks a text holds, CRLF counted once.                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     s : the text, or null.                                             */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : the number of line breaks; 0 for null.                       */
    /*------------------------------------------------------------------------*/
    static int Vln_CountLineBreaks(string s)
    {
        int iCount;                                      // breaks counted so far
        int i;

        /* None counted yet. */
        iCount = 0;
        if (s == null)
        {   /* No text: no breaks. */
            return(0);
        }

        /* Every character looked at once; a CR is counted only when no LF   */
        /* follows it, so CRLF counts once, at its LF -- the same three      */
        /* endings the parser splits lines at.                               */
        for (i = 0; i < s.Length; i++)
        {   /* One character per iteration. */
            if (s[i] == '\n')
            {   /* LF, or the second half of CRLF (counted here). */
                iCount++;
            }
            else if (s[i] == '\r' && (i + 1 >= s.Length || s[i + 1] != '\n'))
            {   /* A lone CR: a break of its own. */
                iCount++;
            }
        }

        /* Every break once. */
        return(iCount);
    }

    /*------------------------------------------------------------------------*/
    /* Vln_ParseLine:                                                         */
    /*                                                                        */
    /* One line's content (indentation and line break removed) as an item,    */
    /* by its format's rules.                                                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iFormat  : the VF_ line format.                                    */
    /*     sContent : the line from its first non-space character to its line */
    /*                break (not empty, not starting with a space or tab).    */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesItem : the item, its lead not yet set.                     */
    /*------------------------------------------------------------------------*/
    static VoorheesItem Vln_ParseLine(int iFormat, string sContent)
    {
        switch (iFormat)
        {   /* The format's own reading of one line. */
            case VoorheesFormat.VF_INI:
                /* INI: comment, section, entry or key alone. */
                return(VoorheesIni.Vin_ParseLine(sContent));

            case VoorheesFormat.VF_KLIPPER:
            case VoorheesFormat.VF_MOONRAKER:
            case VoorheesFormat.VF_KLIPPERSCREEN:
                /* The Klipper family: comment, section or option, by dialect. */
                return(VoorheesCfg.Vcf_ParseLine(iFormat, sContent));

            case VoorheesFormat.VF_REG:
                /* REG: comment, key or value. */
                return(VoorheesReg.Vrg_ParseLine(sContent));

            default:
                /* A line format without a module: never reached (only the    */
                /* line formats' loaders call Vln_Parse).  It throws rather   */
                /* than make up an item.                                      */
                throw new InvalidOperationException(VoorheesFormat.Vf_Name(iFormat)
                    + " lines can't be read yet.");
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vln_Write:                                                             */
    /*                                                                        */
    /* The whole document as text: its header line if it has one (REG), each  */
    /* item's lead and content in order, then the document's closing text     */
    /* (generated when null: a line break after the last item, nothing for an */
    /* empty document; a blank line after a header document's items).  The    */
    /* reverse of Vln_Parse, so an unedited document gives its text back      */
    /* exactly.  Called by each line format's save (VoorheesIni.cs,           */
    /* VoorheesCfg.cs, VoorheesReg.cs) and by the dispatcher                  */
    /* (VoorheesFormat.cs).                                                   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc : the document.                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the file's text.                                          */
    /*------------------------------------------------------------------------*/
    public static string Vln_Write(VoorheesDocument doc)
    {
        StringBuilder sb;                                // the text being built

        /* An empty buffer; then the header line, if the document has one. */
        sb = new StringBuilder();
        if (doc.Vd_root.Vn_sRaw != null)
        {   /* A header line first (REG's), exactly as read -- its line   */
            /* break is the first item's lead, written next.              */
            sb.Append(doc.Vd_root.Vn_sRaw);
        }
        /* Every item, with no limit (so the "stopped" answer is always   */
        /* false and is not looked at), then the closing text.            */
        Vln_WriteItems(doc, doc.Vd_root, sb, int.MaxValue);
        sb.Append(Vln_CloseOf(doc, doc.Vd_root));

        /* The document's text. */
        return(sb.ToString());
    }

    /*------------------------------------------------------------------------*/
    /* Vln_WriteItems:                                                        */
    /*                                                                        */
    /* A container's items, each as its lead (stored or generated) and its    */
    /* content, stopping once the text is longer than a limit.  Public for    */
    /* the Klipper family's auto-saved block, whose sections are written      */
    /* like a file and then prefixed (VoorheesCfg.Vcf_WriteBlock).            */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc       : the document (style for generated text).               */
    /*     container : the container.                                         */
    /*     sb        : where the text goes.                                   */
    /*     iLimit    : stop once sb is longer than this (int.MaxValue for no  */
    /*                 limit).                                                */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when it stopped at the limit.                          */
    /*------------------------------------------------------------------------*/
    public static bool Vln_WriteItems(VoorheesDocument doc,
        VoorheesNode container, StringBuilder sb, int iLimit)
    {
        VoorheesItem item;                               // the item being written
        string sLead;                                    // its lead, stored or generated
        int i;

        for (i = 0; i < container.Vn_Count(); i++)
        {   /* One item per iteration: its lead, then its content. */
            if (sb.Length > iLimit)
            {   /* Enough has been written.  The limit is looked at only    */
                /* between items (and inside sections and blocks), so the   */
                /* text can run past it by one item -- a comment, or an     */
                /* entry with all its continuation lines; the caller that   */
                /* wants an exact length cuts it (Vln_RenderItemLimited).   */
                return(true);
            }

            item = container.Vn_lstItems[i];
            if (item.Vi_sLead != null)
            {   /* The lead as it was read (or kept by an edit): the   */
                /* file's own spacing back exactly.                    */
                sLead = item.Vi_sLead;
            }
            else
            {   /* A new or pasted item (lead null): a lead in the document's   */
                /* style, copied from a sibling of the same shape.              */
                sLead = Vln_GeneratedLead(doc, container, i);
            }
            sb.Append(sLead);

            /* The content after it; the lead goes along, since a generated   */
            /* multi-line value is indented to match it.                      */
            if (Vln_WriteContent(doc, item, sLead, sb, iLimit))
            {   /* The content stopped at the limit: so does this. */
                return(true);
            }
        }

        /* All of them, under the limit. */
        return(false);
    }

    /*------------------------------------------------------------------------*/
    /* Vln_WriteContent:                                                      */
    /*                                                                        */
    /* One item's content, without its lead: a comment's line, an entry's     */
    /* line (the format writes key, delimiter and value), an auto-saved block */
    /* (the format writes it whole), or a section's header line followed by   */
    /* all of its items and its closing text.                                 */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc    : the document.                                             */
    /*     item   : the item.                                                 */
    /*     sLead  : the lead written before it ("" when rendered alone): its  */
    /*              indentation places a generated multi-line value.          */
    /*     sb     : where the text goes.                                      */
    /*     iLimit : stop once sb is longer than this.                         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     bool : true when it stopped at the limit.                          */
    /*------------------------------------------------------------------------*/
    static bool Vln_WriteContent(VoorheesDocument doc, VoorheesItem item,
        string sLead, StringBuilder sb, int iLimit)
    {
        if (item.Vi_iKind == VoorheesItem.VI_COMMENT)
        {   /* A comment: its line as stored, marker and all -- the same   */
            /* for every format, so no module is asked.  Written whole,    */
            /* never "stopped": the next item's limit check sees the       */
            /* length.                                                     */
            sb.Append(item.Vi_sComment);
            return(false);
        }

        if (!item.Vi_node.Vn_IsContainer())
        {   /* An entry: the format writes the line (and any continuation   */
            /* lines), whole, so never "stopped" either.                    */
            Vln_WriteEntryLine(doc, item, sLead, sb);
            return(false);
        }

        if (item.Vi_node.Vn_iKind == VoorheesCfg.VCF_AUTOSAVE)
        {   /* An auto-saved block (kind ids are unique to their format, so   */
            /* the test cannot match another format's container): written     */
            /* whole by its module, which answers whether it stopped.         */
            return(VoorheesCfg.Vcf_WriteBlock(doc, item, sb, iLimit));
        }

        /* A section: its header line by the format, then its items the   */
        /* same way as the document's, then its closing text.             */
        Vln_WriteHeaderLine(doc, item, sb);
        if (Vln_WriteItems(doc, item.Vi_node, sb, iLimit))
        {   /* Stopped inside it: the closing text is not written. */
            return(true);
        }
        sb.Append(Vln_CloseOf(doc, item.Vi_node));

        /* Written in full. */
        return(false);
    }

    /*------------------------------------------------------------------------*/
    /* Vln_WriteEntryLine:                                                    */
    /*                                                                        */
    /* An entry's line, by its format.                                        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc   : the document.                                              */
    /*     item  : the entry.                                                 */
    /*     sLead : the lead written before it.                                */
    /*     sb    : where the text goes.                                       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the line is appended.                                       */
    /*------------------------------------------------------------------------*/
    static void Vln_WriteEntryLine(VoorheesDocument doc, VoorheesItem item,
        string sLead, StringBuilder sb)
    {
        switch (doc.Vd_iFormat)
        {   /* The format writes its own entry line. */
            case VoorheesFormat.VF_INI:
                /* INI: key=value, or the key alone.  No lead is passed:     */
                /* an INI value is one line, so nothing is indented to it.   */
                VoorheesIni.Vin_WriteEntry(doc, item, sb);
                break;

            case VoorheesFormat.VF_KLIPPER:
            case VoorheesFormat.VF_MOONRAKER:
            case VoorheesFormat.VF_KLIPPERSCREEN:
                /* The Klipper family: name, delimiter, value (perhaps several lines), inline comment. */
                VoorheesCfg.Vcf_WriteEntry(doc, item, sLead, sb);
                break;

            case VoorheesFormat.VF_REG:
                /* REG: name, "=", value (hex wrapped as regedit wraps it). */
                VoorheesReg.Vrg_WriteEntry(doc, item, sLead, sb);
                break;

            default:
                /* A line format without a module: never reached (only line   */
                /* formats' documents are written here).  It throws rather    */
                /* than write nothing.                                        */
                throw new InvalidOperationException(VoorheesFormat.Vf_Name(
                    doc.Vd_iFormat) + " lines can't be written yet.");
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vln_WriteHeaderLine:                                                   */
    /*                                                                        */
    /* A section's header line (not its items), by its format.                */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc  : the document.                                               */
    /*     item : the section.                                                */
    /*     sb   : where the text goes.                                        */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the line is appended.                                       */
    /*------------------------------------------------------------------------*/
    static void Vln_WriteHeaderLine(VoorheesDocument doc, VoorheesItem item,
        StringBuilder sb)
    {
        switch (doc.Vd_iFormat)
        {   /* The format writes its own header line. */
            case VoorheesFormat.VF_INI:
                /* INI: [name] and whatever follows it on the line. */
                VoorheesIni.Vin_WriteHeader(item, sb);
                break;

            case VoorheesFormat.VF_KLIPPER:
            case VoorheesFormat.VF_MOONRAKER:
            case VoorheesFormat.VF_KLIPPERSCREEN:
                /* The Klipper family: [name] or [include path], then its comment. */
                VoorheesCfg.Vcf_WriteHeader(item, sb);
                break;

            case VoorheesFormat.VF_REG:
                /* REG: [path] or [-path]. */
                VoorheesReg.Vrg_WriteHeader(item, sb);
                break;

            default:
                /* A line format without a module: never reached, as for     */
                /* entry lines above; it throws rather than write nothing.   */
                throw new InvalidOperationException(VoorheesFormat.Vf_Name(
                    doc.Vd_iFormat) + " lines can't be written yet.");
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vln_CloseOf:                                                           */
    /*                                                                        */
    /* A container's closing text: as stored, or generated -- a section has   */
    /* none (the next item's lead starts the next line); a document with a    */
    /* header line (REG) ends with a blank line, items or not; any other      */
    /* document ends with a line break once it has items, and is empty        */
    /* otherwise.                                                             */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc       : the document.                                          */
    /*     container : the container.                                         */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the closing text.                                         */
    /*------------------------------------------------------------------------*/
    static string Vln_CloseOf(VoorheesDocument doc, VoorheesNode container)
    {
        if (container.Vn_sClose != null)
        {   /* As it was read (or set by the format): the file's own end. */
            return(container.Vn_sClose);
        }

        if (container.Vn_iKind == VoorheesNode.VN_DOCUMENT && container.Vn_sRaw != null)
        {   /* A document with a header line (REG): it ends with a blank   */
            /* line, as regedit's do (as found), in the document's line    */
            /* ending.                                                     */
            return(doc.Vd_sNewline + doc.Vd_sNewline);
        }

        if (container.Vn_iKind == VoorheesNode.VN_DOCUMENT
            && container.Vn_Count() > 0)
        {   /* A document with lines: it ends with a line break, so the   */
            /* last line is a complete one.                               */
            return(doc.Vd_sNewline);
        }

        /* A section (its last line's break is the next item's lead), or   */
        /* an empty document, which writes as nothing at all: no closing   */
        /* text.                                                           */
        return("");
    }

    /*------------------------------------------------------------------------*/
    /* Vln_GeneratedLead:                                                     */
    /*                                                                        */
    /* The lead of an item that has none (a new or pasted one), in the        */
    /* document's style:                                                      */
    /*     o the first item of the document: nothing (it starts the file);    */
    /*     o otherwise the lead of a sibling of the same shape (comment,      */
    /*       entry or section) -- the first one after the container's first   */
    /*       item in the document, and for a section anywhere; any in a       */
    /*       section -- so a new section gets the file's blank-line spacing   */
    /*       and a new entry its indentation;                                 */
    /*     o failing that, one line break (and a blank line before a new      */
    /*       section that is not its container's first item).                 */
    /* Called by Vln_WriteItems for an item whose lead is null.               */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc       : the document.                                          */
    /*     container : the item's container.                                  */
    /*     iPos      : the item's position.                                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the lead.                                                 */
    /*------------------------------------------------------------------------*/
    static string Vln_GeneratedLead(VoorheesDocument doc,
        VoorheesNode container, int iPos)
    {
        VoorheesItem item;                               // the item
        VoorheesItem sibling;                            // a sibling looked at
        bool bDocument;                                  // the container is the document
        int iShape;                                      // the item's shape
        int i;

        /* The two cases where the item is the document's first come first:   */
        /* no sibling's lead is right for them.                               */
        bDocument = (container.Vn_iKind == VoorheesNode.VN_DOCUMENT);
        if (bDocument && iPos == 0 && container.Vn_sRaw != null)
        {   /* The first item after a header line (REG): the line break   */
            /* ending the header, then a blank line between them.         */
            return(doc.Vd_sNewline + doc.Vd_sNewline);
        }

        if (bDocument && iPos == 0)
        {   /* The first line of the file: nothing before it, or the file   */
            /* would start with a blank line.                               */
            return("");
        }

        /* A sibling of the same shape with a lead of its own -- not the      */
        /* container's first item where that one's lead is unlike the rest:   */
        /* the document's (it starts the file) and the first section in an    */
        /* auto-saved block (it follows the header with no blank line).       */
        item = container.Vn_lstItems[iPos];
        iShape = Vln_Shape(item);
        for (i = 0; i < container.Vn_Count(); i++)
        {   /* One sibling per iteration, in order: the first that        */
            /* qualifies wins.  The item itself, a sibling with no lead   */
            /* of its own (another new one), one of another shape, and    */
            /* the excluded first items are passed over.                  */
            sibling = container.Vn_lstItems[i];
            if (i != iPos && !((bDocument || iShape == VLN_SHAPESECTION) && i == 0)
                && sibling.Vi_sLead != null && Vln_Shape(sibling) == iShape)
            {   /* Its lead: the file's spacing and indentation for this shape. */
                return(sibling.Vi_sLead);
            }
        }

        if (iShape == VLN_SHAPESECTION && iPos > 0)
        {   /* A section with something before it, and no section to copy:   */
            /* a blank line sets it apart.                                   */
            return(doc.Vd_sNewline + doc.Vd_sNewline);
        }

        /* Nothing to copy: one line break, in the document's line ending,   */
        /* so the item starts a line of its own with no indentation.         */
        return(doc.Vd_sNewline);
    }

    /*------------------------------------------------------------------------*/
    /* Item shapes, for matching a new item's lead to its siblings' and for   */
    /* the Raw edit's same-shape rule (Vln_ApplyRawEdit).  Only compared with */
    /* each other, so the values themselves mean nothing.                     */
    /*------------------------------------------------------------------------*/

    const int VLN_SHAPECOMMENT = 0;                      // a comment line
    const int VLN_SHAPEENTRY = 1;                        // an entry line
    const int VLN_SHAPESECTION = 2;                      // a section header and its lines

    /*------------------------------------------------------------------------*/
    /* Vln_Shape:                                                             */
    /*                                                                        */
    /* An item's shape: comment, entry or section.                            */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     item : the item.                                                   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     int : a VLN_SHAPE value.                                           */
    /*------------------------------------------------------------------------*/
    static int Vln_Shape(VoorheesItem item)
    {
        if (item.Vi_iKind == VoorheesItem.VI_COMMENT)
        {   /* A comment (it has no value node, so tested first). */
            return(VLN_SHAPECOMMENT);
        }

        if (item.Vi_node.Vn_IsContainer())
        {   /* A section -- or an include, or an auto-saved block, which are   */
            /* containers too and are placed like sections.                    */
            return(VLN_SHAPESECTION);
        }

        /* Anything else is an entry: a member with a plain value. */
        return(VLN_SHAPEENTRY);
    }

    /*------------------------------------------------------------------------*/
    /* Vln_RenderItemLimited:                                                 */
    /*                                                                        */
    /* One item's text as it is (or would be) in the file, without its lead,  */
    /* for the Raw box and the raw tree labels: a comment's or an entry's     */
    /* line; a section's header line and all of its lines.  Stops once the    */
    /* text is longer than a limit.                                           */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc        : the document.                                         */
    /*     lstPath    : the item's position path (not empty).                 */
    /*     iMaxChars  : most characters wanted (int.MaxValue for no limit).   */
    /*     bTruncated : set to true when the text is longer than iMaxChars.   */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the text, at most iMaxChars long; "" when the path names  */
    /*              no item.                                                  */
    /*------------------------------------------------------------------------*/
    public static string Vln_RenderItemLimited(VoorheesDocument doc,
        List<int> lstPath, int iMaxChars, out bool bTruncated)
    {
        VoorheesItem item;                               // the item
        StringBuilder sb;                                // its text

        /* Not cut until shown otherwise; the item the path names. */
        bTruncated = false;
        item = doc.Vd_ItemAt(lstPath);
        if (item == null)
        {   /* No item there: an empty text, not cut. */
            return("");
        }

        /* The content alone, written as Vln_Write would write it, stopping   */
        /* near the limit.  Whether it stopped is not taken from the          */
        /* writer: the length test below answers the same question.           */
        sb = new StringBuilder();
        if (item.Vi_sLead != null)
        {   /* Its own lead (not written) places a generated multi-line   */
            /* value's indentation, as in the file.                       */
            Vln_WriteContent(doc, item, item.Vi_sLead, sb, iMaxChars);
        }
        else
        {   /* A new item: no indentation known, so none. */
            Vln_WriteContent(doc, item, "", sb, iMaxChars);
        }
        /* The writer stops only between items, so it can overshoot. */
        if (sb.Length > iMaxChars)
        {   /* Longer than wanted: cut to exactly iMaxChars, and said so. */
            bTruncated = true;
            return(sb.ToString(0, iMaxChars));
        }

        /* All of it, within the limit. */
        return(sb.ToString());
    }

    /*------------------------------------------------------------------------*/
    /* Vln_ApplyRawEdit:                                                      */
    /*                                                                        */
    /* The item a Raw edit describes: the box's text read by the same rules   */
    /* as a file, which must give exactly one item of the same shape as the   */
    /* one edited (a comment line, an entry line, or a section with its       */
    /* lines), starting at its first character.  Line breaks and blank lines  */
    /* after it are dropped (the next item's lead keeps the file's spacing);  */
    /* everything else is kept exactly as typed, and the item keeps its old   */
    /* lead (its place in the file).  The text is read as if under the        */
    /* item's container, so the format's strict rules judge it in its place   */
    /* (an option is valid in a section).                                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     doc       : the document (its format).                             */
    /*     container : the item's container.                                  */
    /*     oldItem   : the item being edited.                                 */
    /*     sText     : the Raw box's text.                                    */
    /*     sError    : set to why the text is refused (with line and column   */
    /*                 within the box), else null.                            */
    /*                                                                        */
    /* Returns:                                                               */
    /*     VoorheesItem : the new item, or null when refused.                 */
    /*------------------------------------------------------------------------*/
    public static VoorheesItem Vln_ApplyRawEdit(VoorheesDocument doc,
        VoorheesNode container, VoorheesItem oldItem, string sText,
        out string sError)
    {
        VoorheesNode root;                               // the box's text, read
        VoorheesItem item;                               // the one item it must hold
        List<int> lstStarts;                             // where each top-level item starts

        /* The box's text read like a file, but as if under the item's       */
        /* container (the strict rules only), with blocks looked for, so a   */
        /* block can be Raw-edited too.  The top-level items' offsets are    */
        /* asked for, to point at a second item if there is one.             */
        lstStarts = new List<int>();
        root = Vln_ParseLines(doc.Vd_iFormat, sText, lstStarts, true, container,
            out sError);
        if (root == null)
        {   /* Not valid in the format: refused, sError already giving the   */
            /* reason with its line and column within the box.               */
            return(null);
        }

        if (root.Vn_Count() == 0)
        {   /* Nothing but blank lines: no item to put in its place.  Delete   */
            /* is named, since an empty Raw box is not taken as one.           */
            sError = "The Raw box is empty: type the entry, or use Delete to "
                + "remove it.";
            return(null);
        }

        if (root.Vn_Count() > 1)
        {   /* More than one item: one Raw edit replaces one item, so the   */
            /* second one's start is named for the user to find.            */
            sError = Vln_Position(sText, lstStarts[1]) + ": the Raw box holds "
                + "one entry (or one section with its lines); add others "
                + "separately.";
            return(null);
        }

        /* The one item.  Its lead in the box must be empty: the item keeps   */
        /* its old lead below, so anything typed before it would be lost.     */
        item = root.Vn_lstItems[0];
        if (item.Vi_sLead.Length > 0)
        {   /* Indentation or blank lines before it: refused, pointing at   */
            /* the start of the box.                                        */
            sError = "Line 1, column 1: start the Raw text at the entry itself "
                + "(its indentation and the blank lines before it are kept "
                + "from the file).";
            return(null);
        }

        if (Vln_Shape(item) != Vln_Shape(oldItem))
        {   /* A different shape: Raw edits change an item, not its kind of   */
            /* line.  Refused, naming the shape the box must hold.            */
            sError = "The Raw box must hold " + Vln_ShapeName(Vln_Shape(oldItem))
                + ", like the entry being edited.";
            return(null);
        }

        /* A registry file's strings in hex are read by the parse as    */
        /* UTF-16; in a REGEDIT4 file they are ANSI, so they are read   */
        /* again as the whole-file load reads them.                     */
        if (doc.Vd_iFormat == VoorheesFormat.VF_REG)
        {   /* The REG module judges by the document's header. */
            VoorheesReg.VRG_AfterRawEdit(doc, item);
        }

        /* Its place in the file stays the old item's: the old lead, so the   */
        /* spacing and indentation before it are unchanged.  The text after   */
        /* it in the box (the parse's closing text) is not used.              */
        item.Vi_sLead = oldItem.Vi_sLead;

        /* The item exactly as typed, for the caller to put in its place. */
        return(item);
    }

    /*------------------------------------------------------------------------*/
    /* Vln_ShapeName:                                                         */
    /*                                                                        */
    /* A shape in words, for messages.                                        */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     iShape : a VLN_SHAPE value.                                        */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : e.g. "a comment line".                                    */
    /*------------------------------------------------------------------------*/
    static string Vln_ShapeName(int iShape)
    {
        switch (iShape)
        {   /* One phrase per shape, worded to follow "must hold".   */
            /* A comment.                                            */
            case VLN_SHAPECOMMENT: return("a comment line");
            /* A section (an include or a block too). */
            case VLN_SHAPESECTION: return("a section header and its lines");
            /* VLN_SHAPEENTRY, the only other value. */
            default:               return("one entry line");
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vln_Position:                                                          */
    /*                                                                        */
    /* An offset in a text as "Line L, column C" (both from 1), for messages. */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     sText   : the text.                                                */
    /*     iOffset : the offset.                                              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the position in words.                                    */
    /*------------------------------------------------------------------------*/
    public static string Vln_Position(string sText, int iOffset)
    {
        int iLine;                                       // line number so far
        int iLineStart;                                  // where the current line starts
        int i;

        /* Line 1 starts at offset 0; every line break before the offset        */
        /* moves to the next line.  A break is counted as Vln_CountLineBreaks   */
        /* counts one: an LF, or a CR with no LF after it, so CRLF is one.      */
        iLine = 1;
        iLineStart = 0;
        for (i = 0; i < iOffset && i < sText.Length; i++)
        {   /* One character before the offset per iteration. */
            if (sText[i] == '\n'
                || (sText[i] == '\r' && (i + 1 >= sText.Length || sText[i + 1] != '\n')))
            {   /* A line break: the next line starts after it. */
                iLine++;
                iLineStart = i + 1;
            }
        }

        /* One-based line and column (characters, a tab counting one). */
        return("Line " + iLine.ToString(CultureInfo.InvariantCulture)
            + ", column " + (iOffset - iLineStart + 1).ToString(
            CultureInfo.InvariantCulture));
    }

    /*------------------------------------------------------------------------*/
    /* Vln_DetectDelimiter:                                                   */
    /*                                                                        */
    /* The text a file most often puts between a key and its value, delimiter */
    /* included ("=", " = ", ": " ...), for generated entries; every entry    */
    /* with a stored gap, in sections or not, takes part.                     */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     root     : the document node.                                      */
    /*     sDefault : the format's default, for a file with no entries.       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the most common gap (the first seen wins a tie).          */
    /*------------------------------------------------------------------------*/
    public static string Vln_DetectDelimiter(VoorheesNode root, string sDefault)
    {
        Dictionary<string, int> dictCounts;              // gap to times seen
        List<string> lstOrder;                           // gaps in the order first seen

        /* Every stored gap tallied, compared exactly (Ordinal): "=" and      */
        /* " = " are different styles, which is the point.  The order each    */
        /* was first seen is kept for the tie-break.  Called by the INI and   */
        /* Klipper-family loaders (VoorheesIni.cs, VoorheesCfg.cs).           */
        dictCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        lstOrder = new List<string>();
        Vln_CountGaps(root, dictCounts, lstOrder);

        /* The most common one, or the default when no entry had a gap. */
        return(Vln_MostCommon(dictCounts, lstOrder, sDefault));
    }

    /*------------------------------------------------------------------------*/
    /* Vln_CountGaps:                                                         */
    /*                                                                        */
    /* Tallies the key gaps of the entries in a container and its sections.   */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     container  : the container.                                        */
    /*     dictCounts : gap to times seen (added to).                         */
    /*     lstOrder   : gaps in the order first seen (added to).              */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the tallies are updated.                                    */
    /*------------------------------------------------------------------------*/
    static void Vln_CountGaps(VoorheesNode container,
        Dictionary<string, int> dictCounts, List<string> lstOrder)
    {
        VoorheesItem item;                               // an item looked at
        int i;

        for (i = 0; i < container.Vn_Count(); i++)
        {   /* One item per iteration, in file order (which decides ties). */
            item = container.Vn_lstItems[i];
            if (item.Vi_iKind != VoorheesItem.VI_MEMBER)
            {   /* A comment: no gap. */
                continue;
            }

            if (item.Vi_node.Vn_IsContainer())
            {   /* A section (or include, or block): its entries, counted   */
                /* into the same tally.                                     */
                Vln_CountGaps(item.Vi_node, dictCounts, lstOrder);
            }
            else if (item.Vi_sKeyGap != null)
            {   /* An entry with a delimiter as written.  A key alone, or an   */
                /* entry made new (gap null), says nothing about the file's    */
                /* style and is skipped.                                       */
                Vln_Tally(dictCounts, lstOrder, item.Vi_sKeyGap);
            }
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vln_DetectCommentPrefix:                                               */
    /*                                                                        */
    /* The comment marker a file uses most (the first character of its        */
    /* comment lines, such as ";" or "#"), for new comments.                  */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     root     : the document node.                                      */
    /*     sDefault : the format's default, for a file with no comments.      */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the most common marker (the first seen wins a tie).       */
    /*------------------------------------------------------------------------*/
    public static string Vln_DetectCommentPrefix(VoorheesNode root,
        string sDefault)
    {
        Dictionary<string, int> dictCounts;              // marker to times seen
        List<string> lstOrder;                           // markers in the order first seen

        /* Every comment line's first character tallied, exactly, with the   */
        /* order first seen for the tie-break.  Called by the INI and        */
        /* Klipper-family loaders (VoorheesIni.cs, VoorheesCfg.cs).          */
        dictCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        lstOrder = new List<string>();
        Vln_CountPrefixes(root, dictCounts, lstOrder);

        /* The most common one, or the default when there is no comment. */
        return(Vln_MostCommon(dictCounts, lstOrder, sDefault));
    }

    /*------------------------------------------------------------------------*/
    /* Vln_CountPrefixes:                                                     */
    /*                                                                        */
    /* Tallies the first characters of the comment items in a container and   */
    /* its sections.                                                          */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     container  : the container.                                        */
    /*     dictCounts : marker to times seen (added to).                      */
    /*     lstOrder   : markers in the order first seen (added to).           */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the tallies are updated.                                    */
    /*------------------------------------------------------------------------*/
    static void Vln_CountPrefixes(VoorheesNode container,
        Dictionary<string, int> dictCounts, List<string> lstOrder)
    {
        VoorheesItem item;                               // an item looked at
        int i;

        for (i = 0; i < container.Vn_Count(); i++)
        {   /* One item per iteration, in file order (which decides ties). */
            item = container.Vn_lstItems[i];
            if (item.Vi_iKind == VoorheesItem.VI_COMMENT)
            {   /* A comment line: its marker, the first character.            */
                /* Substring(0, 1) needs the comment not empty: every format   */
                /* reads one only from a line starting with its marker         */
                /* (VoorheesIni.cs, VoorheesCfg.cs, VoorheesReg.cs), and       */
                /* INI's made ones are at least the marker; that the other     */
                /* formats' made ones are too is not established here.         */
                Vln_Tally(dictCounts, lstOrder, item.Vi_sComment.Substring(0, 1));
            }
            else if (item.Vi_node.Vn_IsContainer())
            {   /* A section (or include, or block): its comments. */
                Vln_CountPrefixes(item.Vi_node, dictCounts, lstOrder);
            }
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vln_Tally:                                                             */
    /*                                                                        */
    /* Counts one sighting of a value.  Public: VoorheesCfg.cs tallies its    */
    /* continuation indents with it too.                                      */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     dictCounts : value to times seen.                                  */
    /*     lstOrder   : values in the order first seen.                       */
    /*     sValue     : the value seen.                                       */
    /*                                                                        */
    /* Returns:                                                               */
    /*     void : the tally is updated.                                       */
    /*------------------------------------------------------------------------*/
    public static void Vln_Tally(Dictionary<string, int> dictCounts,
        List<string> lstOrder, string sValue)
    {
        int iCount;                                      // times seen before

        if (dictCounts.TryGetValue(sValue, out iCount))
        {   /* Seen before: one more.  Its place in lstOrder stays where it   */
            /* was first seen.                                                */
            dictCounts[sValue] = iCount + 1;
        }
        else
        {   /* New: first sighting, and its place in the order -- the   */
            /* order Vln_MostCommon breaks a tie by.                    */
            dictCounts[sValue] = 1;
            lstOrder.Add(sValue);
        }
    }

    /*------------------------------------------------------------------------*/
    /* Vln_MostCommon:                                                        */
    /*                                                                        */
    /* The value seen most often, the earliest seen winning a tie.  Public:   */
    /* VoorheesCfg.cs picks its continuation indent with it too.              */
    /*                                                                        */
    /* Arguments:                                                             */
    /*     dictCounts : value to times seen.                                  */
    /*     lstOrder   : values in the order first seen.                       */
    /*     sDefault   : the answer when nothing was seen.                     */
    /*                                                                        */
    /* Returns:                                                               */
    /*     string : the value.                                                */
    /*------------------------------------------------------------------------*/
    public static string Vln_MostCommon(Dictionary<string, int> dictCounts,
        List<string> lstOrder, string sDefault)
    {
        string sBest;                                    // the best so far
        int iBest;                                       // its count
        int i;

        /* The default with a count of 0, so any value seen at all beats   */
        /* it, and the default stands only when nothing was seen.          */
        sBest = sDefault;
        iBest = 0;
        for (i = 0; i < lstOrder.Count; i++)
        {   /* One value per iteration, in the order first seen. */
            if (dictCounts[lstOrder[i]] > iBest)
            {   /* Seen more often than the best so far.  Strictly more:   */
                /* an equal count keeps the earlier value, which is the    */
                /* tie-break.                                              */
                sBest = lstOrder[i];
                iBest = dictCounts[lstOrder[i]];
            }
        }

        /* The winner, or the default. */
        return(sBest);
    }
}
