/*-----------------------------------------------------------------------------*/
/* PaneSorter.cs                                                               */
/*                                                                             */
/* SPDX-License-Identifier: GPL-2.0-or-later                                   */
/* Copyright (c) 2026 B. C. Services                                           */
/*                                                                             */
/* The order of the rows in a pane's list: which column they are sorted by,    */
/* and whether ascending or descending.  One of these is given to each list    */
/* as its item sorter; "Sort By" on the right-click menu and a click on a      */
/* column heading change its two settings and sort the list again.             */
/*                                                                             */
/* THE RULES, the same as Explorer's:                                          */
/*                                                                             */
/*   - The ".." row is always first, whatever the order.                       */
/*   - Folders come before everything else, whatever the order; the chosen     */
/*     column orders the folders among themselves and the rest among           */
/*     themselves.                                                             */
/*   - Two rows equal in the chosen column fall back to their names, so the    */
/*     order is always the same for the same rows.                             */
/*                                                                             */
/* HOW A COLUMN IS COMPARED.  The Size column (column 1 in both panes) is      */
/* compared as a NUMBER, read from the cell's digits (the cells carry          */
/* thousands separators).  Every other column is compared as the text it       */
/* shows; the Modified column's text is year-first, so text order is time      */
/* order.  Names in the image pane are compared by ordinal value, because      */
/* ext4 names are case-sensitive bytes; names in the Windows pane ignore       */
/* case, as Windows does.                                                      */
/*                                                                             */
/* WHERE IT IS NOT EXPLORER'S.  The three rules above are; the name order is   */
/* not -- Explorer orders the digits inside names by value ("file2" before     */
/* "file10"), and this compares character by character ("file10" first).       */
/* And text order is not number order for the image pane's Owner column,       */
/* which shows "uid:gid": "1000:1000" sorts before "999:999".  Both follow     */
/* from the rule above and are stated so neither is mistaken for a bug in      */
/* the code; why the numeric name order was not chosen is not recorded.        */
/*                                                                             */
/* The dependencies on MainForm.cs, which builds the rows: the ".." row is     */
/* the only row with a null Tag; a Windows folder's or drive's Tag is its      */
/* path (a string) and a file's a FileInfo; an image row's Tag is its          */
/* EEN_stEntry; Size is column 1 and Name column 0 in both panes.  A change to */
/* any of those must change this file too.                                     */
/*-----------------------------------------------------------------------------*/

/* The framework: the base types (the two string comparisons the panes'      */
/* names use), and the list rows themselves, whose cells and Tag are read.   */
/* The IComparer the list view needs is named in full on the class.          */
using System;
using System.Windows.Forms;

namespace BifrostFM
{
    /*------------------------------------------------------------------------*/
    /* PSR_clsPaneSorter -- one pane's row order (the file header gives the   */
    /* rules).  The list view takes it through the non-generic IComparer,     */
    /* whose Compare is here with the rest of the sorter.                     */
    /*                                                                        */
    /* Implements IComparer because the framework requires it:                */
    /* ListView.ListViewItemSorter takes only an IComparer, so the list's own */
    /* sort reaches this order only through it -- a framework-demanded        */
    /* interface, the one kind allowed here, never an interface of ours.      */
    /*------------------------------------------------------------------------*/
    internal sealed class PSR_clsPaneSorter : System.Collections.IComparer
    {
        /* The two columns compared their own way -- documentation table.   */
        /* Their numbers must match the panes' columns (MainForm.cs).       */
        private const int PSR_COLUMN_NAME = 0;     // the Name column, in both panes
        private const int PSR_COLUMN_SIZE = 1;     // the Size column, in both panes: compared as a number

        /* The order asked for, and which pane -- documentation table.     */
        /* The main window sets the column and the direction; which pane   */
        /* is fixed when the sorter is made.                               */
        internal int  PSR_iColumn;        // the column the rows are sorted by; set by MainForm.cs (Sort By, a heading click), which then has the list sort again
        internal bool PSR_bDescending;    // true for descending order; set the same way
        private  bool Psr_bImagePane;     // true for the image pane's list (names by ordinal value), false for the Windows pane's

        /*--------------------------------------------------------------------*/
        /* PSR_clsPaneSorter (constructor):                                   */
        /*                                                                    */
        /* Create a sorter for one pane's list.  The pane is fixed for the    */
        /* sorter's life -- it decides the name rule -- while the column and  */
        /* the direction are fields the pane's owner changes later (MainForm  */
        /* makes one per pane from the saved settings and re-sorts after a    */
        /* change).                                                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     bImagePane  : true for the image pane, false for the Windows   */
        /*                   pane.                                            */
        /*     iColumn     : the column to sort by at first.                  */
        /*     bDescending : true to start in descending order.               */
        /*                                                                    */
        /* Returns:                                                           */
        /*     (constructor) : the sorter.                                    */
        /*--------------------------------------------------------------------*/
        internal PSR_clsPaneSorter(bool bImagePane, int iColumn, bool bDescending)
        {
            /* The three facts, as given; nothing is checked -- a column past   */
            /* a row's last cell compares as "" (Psr_CellText).                 */
            Psr_bImagePane  = bImagePane;
            PSR_iColumn     = iColumn;
            PSR_bDescending = bDescending;
        }

        /*--------------------------------------------------------------------*/
        /* Psr_IsFolder:                                                      */
        /*                                                                    */
        /* Whether a row is a folder, in either pane.  An image symbolic link */
        /* is not a folder here, even one that leads to a directory: its type */
        /* is "link", and it sorts with the files.                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lviRow : a row that is not the ".." row.                       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true for a folder, a drive or an image directory.       */
        /*--------------------------------------------------------------------*/
        private static bool Psr_IsFolder(ListViewItem lviRow)
        {
            /* The panes mark their rows differently, so the Tag's type is   */
            /* tested: a Windows folder's (or drive's) Tag is its path, a    */
            /* string, where a file's is a FileInfo; an image row's Tag is   */
            /* its EEN_stEntry, whose type says.                             */
            if (lviRow.Tag is string)
            {   /* A Windows folder or drive: MainForm.cs tags those rows with   */
                /* their path, and only those, so a string Tag means a folder.   */
                return(true);
            }

            if (lviRow.Tag is EEN_stEntry)
            {   /* An image entry: a folder exactly when the engine says its   */
                /* type is a directory.                                        */
                return(((EEN_stEntry)lviRow.Tag).EEN_uiType == ENG_clsNative.ENG_TYPE_DIR);
            }

            /* A Windows file (its Tag a FileInfo) -- or any Tag this test   */
            /* does not know, which therefore sorts with the files.          */
            return(false);
        }

        /*--------------------------------------------------------------------*/
        /* Psr_CellText:                                                      */
        /*                                                                    */
        /* The text a row shows in a column; "" when the row has no cell      */
        /* there.                                                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lviRow  : the row.                                             */
        /*     iColumn : the column.                                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : the cell's text.                                      */
        /*--------------------------------------------------------------------*/
        private static string Psr_CellText(ListViewItem lviRow, int iColumn)
        {
            if (iColumn >= lviRow.SubItems.Count)
            {   /* No cell in that column: "" rather than an exception, so a   */
                /* short row sorts first in ascending order.                   */
                return("");
            }

            /* The cell's text (column 0 is the row's own text). */
            return(lviRow.SubItems[iColumn].Text);
        }

        /*--------------------------------------------------------------------*/
        /* Psr_CellNumber:                                                    */
        /*                                                                    */
        /* A Size cell as a number: its digits, with whatever separates the   */
        /* thousands left out.  An empty cell (a folder shows no size) is 0.  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     sCell : the cell's text.                                       */
        /*                                                                    */
        /* Returns:                                                           */
        /*     ulong : the number the cell shows.                             */
        /*--------------------------------------------------------------------*/
        private static ulong Psr_CellNumber(string sCell)
        {
            ulong ullValue;
            int   i;

            /* Nothing read yet: an empty cell stays 0. */
            ullValue = 0;

            /* Left to right, each digit shifting the value one decimal       */
            /* place; anything that is not a digit is passed over. The pane   */
            /* supplies a byte count already representable as ulong.          */
            /* Taking the digits alone, rather than parsing with the          */
            /* culture's rules, reads the cell whatever the user's            */
            /* separator is (a comma, a point, a space); it relies on the     */
            /* cell holding a plain count of bytes ("N0" in MainForm.cs)      */
            /* -- a "1.5 MB" style would read as 15.                          */
            for (i = 0; i < sCell.Length; i++)
            {   /* One character of the cell per iteration. */
                if (sCell[i] >= '0' && sCell[i] <= '9')
                {   /* A digit: the next decimal place. */
                    ullValue = (ullValue * 10) + (ulong)(sCell[i] - '0');
                }
            }

            /* The cell's value, as a number the comparison can order. */
            return(ullValue);
        }

        /*--------------------------------------------------------------------*/
        /* Psr_CompareNames:                                                  */
        /*                                                                    */
        /* Compare two rows by name, by this pane's rule: ordinal value in    */
        /* the image pane, ignoring case in the Windows pane.                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lviLeft  : one row.                                            */
        /*     lviRight : another.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : negative, zero or positive.                              */
        /*--------------------------------------------------------------------*/
        private int Psr_CompareNames(ListViewItem lviLeft, ListViewItem lviRight)
        {
            if (Psr_bImagePane)
            {   /* ext4 names: case-sensitive bytes.  The row holds the name   */
                /* decoded to UTF-16, and an ordinal compare of that is the    */
                /* UTF-8 bytes' order for every character but one corner:      */
                /* characters past U+FFFF sort before U+E000 to U+FFFF here.   */
                return(string.CompareOrdinal(lviLeft.Text, lviRight.Text));
            }

            /* Windows names: case does not distinguish them, so "a" and "B"   */
            /* sort as "A" and "B" would.                                      */
            return(string.Compare(lviLeft.Text, lviRight.Text, StringComparison.OrdinalIgnoreCase));
        }

        /*--------------------------------------------------------------------*/
        /* Compare:                                                           */
        /*                                                                    */
        /* The list view's comparison, in the exact non-generic signature     */
        /* IComparer requires (two objects, the rows): hands both to          */
        /* Psr_Compare below, which holds the ordering -- folders first, the  */
        /* chosen column, the direction.                                      */
        /*                                                                    */
        /* Public, and named Compare without a group prefix, because the      */
        /* framework requires it: it is IComparer's one method, which the     */
        /* list view's sort calls by that name.                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objLeft  : one row (a ListViewItem).                           */
        /*     objRight : another.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : negative when the left row comes first, positive when    */
        /*           the right one does, zero for equal ordering keys.        */
        /*--------------------------------------------------------------------*/
        public int Compare(object objLeft, object objRight)
        {
            /* The ordering itself is in the helper. */
            return(Psr_Compare(objLeft, objRight));
        }

        /*--------------------------------------------------------------------*/
        /* Psr_Compare:                                                       */
        /*                                                                    */
        /* The list view's question: which of two rows comes first, by the    */
        /* rules the file header lists.                                       */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objLeft  : one row (a ListViewItem).                           */
        /*     objRight : another.                                            */
        /*                                                                    */
        /* Returns:                                                           */
        /*     int : negative when the left row comes first, positive when    */
        /*           the right one does, zero for equal ordering keys.        */
        /*--------------------------------------------------------------------*/
        private int Psr_Compare(object objLeft, object objRight)
        {
            ListViewItem lviLeft;
            ListViewItem lviRight;
            ulong        ullLeft;
            ulong        ullRight;
            bool         bLeftFolder;
            bool         bRightFolder;
            int          iOrder;

            /* The list view hands its own rows; the casts cannot fail. */
            lviLeft  = (ListViewItem)objLeft;
            lviRight = (ListViewItem)objRight;

            /* The ".." row (its Tag is null) leads, always -- tested before   */
            /* the direction is looked at, so descending order cannot move     */
            /* it.  Two null Tags can only be the same row, the list holding   */
            /* one ".." at most.                                               */
            if (lviLeft.Tag == null && lviRight.Tag == null)
            {   /* The same row, compared with itself: equal. */
                return(0);
            }

            if (lviLeft.Tag == null)
            {   /* The left row is "..": it comes first. */
                return(-1);
            }

            if (lviRight.Tag == null)
            {   /* The right row is "..": it comes first. */
                return(1);
            }

            /* Folders before the rest, always -- again before the direction   */
            /* is applied, so descending order reverses each group, never      */
            /* the groups.                                                     */
            bLeftFolder  = Psr_IsFolder(lviLeft);
            bRightFolder = Psr_IsFolder(lviRight);

            if (bLeftFolder && !bRightFolder)
            {   /* A folder sorts before a non-folder. */
                return(-1);
            }

            if (!bLeftFolder && bRightFolder)
            {   /* A non-folder sorts after a folder. */
                return(1);
            }

            /* The same group: the chosen column decides. */
            if (PSR_iColumn == PSR_COLUMN_NAME)
            {   /* By name, by this pane's rule: ext4 names exactly, Windows   */
                /* names ignoring case.                                        */
                iOrder = Psr_CompareNames(lviLeft, lviRight);
            }
            else if (PSR_iColumn == PSR_COLUMN_SIZE)
            {   /* By size: each Size cell read back as a number, since its   */
                /* text would put "9" after "10" and is broken by thousands   */
                /* separators.  A folder's empty cell reads as 0.             */
                ullLeft  = Psr_CellNumber(Psr_CellText(lviLeft, PSR_iColumn));
                ullRight = Psr_CellNumber(Psr_CellText(lviRight, PSR_iColumn));
                iOrder   = ullLeft.CompareTo(ullRight);
            }
            else
            {   /* By the text the column shows -- ordinal, so the result    */
                /* does not change with the user's language settings.  The   */
                /* Owner column's numbers order as text (see the header).    */
                iOrder = string.CompareOrdinal(Psr_CellText(lviLeft, PSR_iColumn), Psr_CellText(lviRight, PSR_iColumn));
            }

            if (iOrder == 0)
            {   /* Equal in that column: names keep the order steady, so the   */
                /* order of two rows equal in the column never depends on      */
                /* how the list happened to hold them before the sort.         */
                iOrder = Psr_CompareNames(lviLeft, lviRight);
            }

            if (PSR_bDescending)
            {   /* Descending reverses the order within the group -- the      */
                /* name tie-break included, so a descending list is exactly   */
                /* the ascending one turned over.                             */
                iOrder = -iOrder;
            }

            /* The rows' order, which the list view sorts by. */
            return(iOrder);
        }
    }
}
