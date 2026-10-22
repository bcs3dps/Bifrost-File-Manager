/*----------------------------------------------------------------------------*/
/* bfe_util.c                                                                 */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The engine's text helpers: building paths on each side of the boundary,    */
/* converting names between the image's UTF-8 and the host's UTF-16, and      */
/* deciding whether an ext4 name can exist as a Windows file name at all.     */
/*                                                                            */
/* The conversions are written out here rather than taken from the Windows    */
/* API: UTF-8 and UTF-16 are byte formats, not Windows concepts, and doing    */
/* them in plain C keeps this file free of any platform header.               */
/*                                                                            */
/* ONE PLATFORM ASSUMPTION REMAINS, in the type rather than a header: a       */
/* wchar_t is taken to be one 16-bit UTF-16 unit, as it is on Windows (and    */
/* under MinGW, which builds this engine).  Where wchar_t is 32 bits the      */
/* conversions would still compile, and would write surrogate PAIRS where a   */
/* single 32-bit code point belongs.                                          */
/*                                                                            */
/* Every function that returns a pointer returns malloc'd memory, sized       */
/* exactly, that the caller frees -- there is no fixed path buffer anywhere   */
/* in the engine, so no path is ever truncated or refused for its length.     */
/*                                                                            */
/* No function here tests its arguments for NULL: every caller is inside the  */
/* engine and passes strings it has already checked or built itself.          */
/*----------------------------------------------------------------------------*/

/* The C library only: memory, byte strings, and wide strings -- no   */
/* platform header, as the file header says.                          */
#include <stdlib.h>
#include <string.h>
#include <wchar.h>

/* The engine's shared declarations: these functions' prototypes, TRUE /   */
/* FALSE.                                                                  */
#include "bfe_internal.h"

/*----------------------------------------------------------------------------*/
/* BFU_JoinImage:                                                             */
/*                                                                            */
/* Compose the image path of a directory's child, without doubling the root's */
/* slash.  Also the second half of BFU_JoinRelative, whose deeper manifest    */
/* paths follow the same rule.                                                */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszDir  : the parent directory's image path ("/" or "/a/b").           */
/*     pszName : the child's name.                                            */
/*                                                                            */
/* Returns:                                                                   */
/*     char * : malloc'd "<dir>/<name>", or NULL when out of memory.          */
/*----------------------------------------------------------------------------*/
char *BFU_JoinImage(const char *pszDir, const char *pszName)
{
    char   *pszOut;
    size_t  cbDir;
    size_t  cbName;

    /* Measure both parts; the lengths size the buffer exactly. */
    cbDir  = strlen(pszDir);
    cbName = strlen(pszName);

    if (cbDir > 0 && pszDir[cbDir - 1] == '/')
    {   /* The parent already ends in a slash -- the root, the one directory   */
        /* whose path does: that slash is dropped and the one below is the     */
        /* only one.  ("/a/" would work as "/a" too; only the root is ever     */
        /* passed that way.)                                                   */
        cbDir--;
    }

    /* parent + '/' + name + NUL. */
    pszOut = (char *)malloc(cbDir + 1 + cbName + 1);

    if (pszOut == NULL)
    {   /* Out of memory. */
        return(NULL);
    }

    /* Assemble: the parent (less any trailing slash), one slash, then the   */
    /* name together with its NUL, which terminates the result.              */
    memcpy(pszOut, pszDir, cbDir);
    pszOut[cbDir] = '/';
    memcpy(pszOut + cbDir + 1, pszName, cbName + 1);

    /* The caller owns and frees the composed path. */
    return(pszOut);
}

/*----------------------------------------------------------------------------*/
/* BFU_JoinHost:                                                              */
/*                                                                            */
/* Compose the host path of a folder's child.  The separator is always a      */
/* backslash, never '/': a host path may be in the extended-length form       */
/* ("\\?\", which lifts the 260-character limit), and Windows does not        */
/* translate forward slashes inside one.  The program itself passes ordinary  */
/* paths; the backslash is right for both, so nothing here has to tell them   */
/* apart.                                                                     */
/*                                                                            */
/* Arguments:                                                                 */
/*     pwszDir  : the parent folder's host path.                              */
/*     pwszName : the child's name.                                           */
/*                                                                            */
/* Returns:                                                                   */
/*     wchar_t * : malloc'd "<dir>\<name>", or NULL when out of memory.       */
/*----------------------------------------------------------------------------*/
wchar_t *BFU_JoinHost(const wchar_t *pwszDir, const wchar_t *pwszName)
{
    wchar_t *pwszOut;
    size_t   cchDir;
    size_t   cchName;

    /* Measure both parts, in UTF-16 units. */
    cchDir  = wcslen(pwszDir);
    cchName = wcslen(pwszName);

    if (cchDir > 0 && (pwszDir[cchDir - 1] == L'\\' || pwszDir[cchDir - 1] == L'/'))
    {   /* The parent already ends in a separator -- a drive root, the one   */
        /* folder whose path does: it is dropped, so the joined path holds   */
        /* one separator between the root and the name, never two.           */
        cchDir--;
    }

    /* parent + '\' + name + NUL. */
    pwszOut = (wchar_t *)malloc((cchDir + 1 + cchName + 1) * sizeof(wchar_t));

    if (pwszOut == NULL)
    {   /* Out of memory. */
        return(NULL);
    }

    /* Assemble: the parent (less any trailing separator), one backslash,   */
    /* then the name together with its NUL, which terminates the result.    */
    memcpy(pwszOut, pwszDir, cchDir * sizeof(wchar_t));
    pwszOut[cchDir] = L'\\';
    memcpy(pwszOut + cchDir + 1, pwszName, (cchName + 1) * sizeof(wchar_t));

    /* The caller owns and frees the composed path. */
    return(pwszOut);
}

/*----------------------------------------------------------------------------*/
/* BFU_JoinRelative:                                                          */
/*                                                                            */
/* Compose the path a manifest records for a child.  Manifest paths are       */
/* relative to the root of the copy, which is itself recorded as ".", so a    */
/* child of the root is just its name -- the same form an extract has always  */
/* written -- and anything deeper is "<parent>/<name>".                       */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszParent : the parent's manifest path ("." at the root of the copy).  */
/*     pszName   : the child's name.                                          */
/*                                                                            */
/* Returns:                                                                   */
/*     char * : the malloc'd manifest path, or NULL when out of memory.       */
/*----------------------------------------------------------------------------*/
char *BFU_JoinRelative(const char *pszParent, const char *pszName)
{
    char   *pszOut;
    size_t  cbName;

    if (strcmp(pszParent, ".") == 0)
    {   /* A child of the copy's root, told by the exact text "." only: its   */
        /* manifest path is its bare name.  Joining here with BFU_JoinImage   */
        /* would give "./name", a different string for the same item, and     */
        /* an import matching manifest records by path would miss it.  A      */
        /* copy of the name is made even so, because the caller frees what    */
        /* this returns exactly as it frees a joined path.                    */
        cbName = strlen(pszName);
        pszOut = (char *)malloc(cbName + 1);

        if (pszOut != NULL)
        {   /* Copy the name with its NUL. */
            memcpy(pszOut, pszName, cbName + 1);
        }

        /* The copy, or NULL when out of memory. */
        return(pszOut);
    }

    /* Deeper down the rule is the image-path rule: parent, slash, name. */
    return(BFU_JoinImage(pszParent, pszName));
}

/*----------------------------------------------------------------------------*/
/* BFU_AppendHost:                                                            */
/*                                                                            */
/* Append a suffix to a host path -- how a single file's metadata sidecar is  */
/* named ("<hostfile>" + ".ext4meta", bfe_copy.c's BFC_SIDECAR_SUFFIX).       */
/* Nothing is inserted between the two: the suffix brings its own dot.        */
/*                                                                            */
/* Arguments:                                                                 */
/*     pwszPath   : the host path.                                            */
/*     pwszSuffix : the text to append.                                       */
/*                                                                            */
/* Returns:                                                                   */
/*     wchar_t * : the malloc'd result, or NULL when out of memory.           */
/*----------------------------------------------------------------------------*/
wchar_t *BFU_AppendHost(const wchar_t *pwszPath, const wchar_t *pwszSuffix)
{
    wchar_t *pwszOut;
    size_t   cchPath;
    size_t   cchSuffix;

    /* Measure both parts, in UTF-16 units. */
    cchPath   = wcslen(pwszPath);
    cchSuffix = wcslen(pwszSuffix);

    /* path + suffix + NUL. */
    pwszOut = (wchar_t *)malloc((cchPath + cchSuffix + 1) * sizeof(wchar_t));

    if (pwszOut == NULL)
    {   /* Out of memory. */
        return(NULL);
    }

    /* Assemble: the path, then the suffix together with its NUL. */
    memcpy(pwszOut, pwszPath, cchPath * sizeof(wchar_t));
    memcpy(pwszOut + cchPath, pwszSuffix, (cchSuffix + 1) * sizeof(wchar_t));

    /* The caller owns and frees the result. */
    return(pwszOut);
}

/*----------------------------------------------------------------------------*/
/* BFU_Utf8ToWide:                                                            */
/*                                                                            */
/* Convert a UTF-8 string to UTF-16.  An ext4 name is really just bytes, and  */
/* nothing stops one from being malformed UTF-8; such a name has no UTF-16    */
/* spelling, so the conversion REFUSES it (NULL) and the caller reports the   */
/* entry as unrepresentable rather than inventing a host name for it.         */
/*                                                                            */
/* Refused: a stray continuation byte, a truncated sequence, an overlong      */
/* encoding, a code point above U+10FFFF, and an encoded surrogate.           */
/*                                                                            */
/* The decoder is strict (RFC 3629): each refusal closes a way for two        */
/* different byte strings to come out as the same host name, or for a name    */
/* to smuggle a character past BFU_HostNameValid in a longer spelling.        */
/*                                                                            */
/* NULL also means out of memory, and *pbNoMemory tells the two apart: a      */
/* name that cannot cross is a skip, while an allocation failure is an error  */
/* the caller must report as one, or a copy would end "done" with items       */
/* missing.                                                                   */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszUtf8    : the NUL-terminated UTF-8 text.                            */
/*     pbNoMemory : receives TRUE when NULL is returned because memory ran    */
/*                  out, FALSE otherwise (malformed input, or success).       */
/*                                                                            */
/* Returns:                                                                   */
/*     wchar_t * : the malloc'd UTF-16 text, or NULL on malformed input or    */
/*                 when out of memory (*pbNoMemory says which).               */
/*----------------------------------------------------------------------------*/
wchar_t *BFU_Utf8ToWide(const char *pszUtf8, int *pbNoMemory)
{
    const unsigned char *pb;
    wchar_t             *pwszOut;
    size_t               cbIn;
    size_t               iIn;
    size_t               iOut;
    uint32_t             uiCode;
    uint32_t             uiMin;
    int                  nMore;
    int                  k;

    /* Read the input as unsigned bytes, so the lead-byte tests below see   */
    /* 0x80 and above as such and not as negative chars.                    */
    pb   = (const unsigned char *)pszUtf8;
    cbIn = strlen(pszUtf8);

    /* Not out of memory unless the allocation below fails: every other   */
    /* NULL return is a malformed name.                                   */
    *pbNoMemory = FALSE;

    /* A UTF-16 string never has more units than its UTF-8 form has bytes, so   */
    /* the byte count plus the NUL is always enough room.                       */
    pwszOut = (wchar_t *)malloc((cbIn + 1) * sizeof(wchar_t));

    if (pwszOut == NULL)
    {   /* Out of memory: said so, apart from a malformed name. */
        *pbNoMemory = TRUE;
        return(NULL);
    }

    /* Both cursors start at the beginning: iIn in bytes, iOut in units. */
    iIn  = 0;
    iOut = 0;

    while (iIn < cbIn)
    {   /* One code point per iteration. */

        /* STEP 1: read the lead byte -- it gives the sequence length and the   */
        /* smallest code point that length may legally encode.                  */
        if (pb[iIn] < 0x80)
        {   /* 0xxxxxxx: plain ASCII, one byte. */
            uiCode = pb[iIn];
            nMore  = 0;
            uiMin  = 0;
        }
        else if ((pb[iIn] & 0xE0) == 0xC0)
        {   /* 110xxxxx: a two-byte sequence, U+0080 and up. */
            uiCode = pb[iIn] & 0x1Fu;
            nMore  = 1;
            uiMin  = 0x80;
        }
        else if ((pb[iIn] & 0xF0) == 0xE0)
        {   /* 1110xxxx: a three-byte sequence, U+0800 and up. */
            uiCode = pb[iIn] & 0x0Fu;
            nMore  = 2;
            uiMin  = 0x800;
        }
        else if ((pb[iIn] & 0xF8) == 0xF0)
        {   /* 11110xxx: a four-byte sequence, U+10000 and up. */
            uiCode = pb[iIn] & 0x07u;
            nMore  = 3;
            uiMin  = 0x10000;
        }
        else
        {   /* A continuation byte with no lead, or 0xF8 and above: malformed. */

            /* Refuse: free the half-converted output and return NULL. */
            free(pwszOut);
            return(NULL);
        }

        /* Past the lead byte. */
        iIn++;

        /* STEP 2: fold in the continuation bytes, each 10xxxxxx. */
        for (k = 0; k < nMore; k++)
        {   /* One continuation byte per iteration. */
            if (iIn >= cbIn || (pb[iIn] & 0xC0) != 0x80)
            {   /* The sequence is cut short or interrupted: malformed. */

                /* Refuse: free the half-converted output, return NULL. */
                free(pwszOut);
                return(NULL);
            }

            /* Six more bits of the code point, then past this byte. */
            uiCode = (uiCode << 6) | (pb[iIn] & 0x3Fu);
            iIn++;
        }

        /* STEP 3: reject what a decoder must not accept.  The lead bytes      */
        /* 0xC0 / 0xC1 (always overlong) and 0xF5 to 0xF7 (always beyond       */
        /* U+10FFFF) are not refused in STEP 1; they are caught here, by the   */
        /* value they produce, along with every other overlong form.           */
        if (uiCode < uiMin || uiCode > 0x10FFFF || (uiCode >= 0xD800 && uiCode <= 0xDFFF))
        {   /* Overlong, beyond Unicode, or an encoded surrogate. */

            /* Refuse: free the half-converted output and return NULL. */
            free(pwszOut);
            return(NULL);
        }

        /* STEP 4: write it as one UTF-16 unit, or as a surrogate pair. */
        if (uiCode < 0x10000)
        {   /* The Basic Multilingual Plane: one unit, the value itself. */
            pwszOut[iOut] = (wchar_t)uiCode;
            iOut++;
        }
        else
        {   /* Above U+FFFF: high surrogate then low, 10 bits each. */
            uiCode -= 0x10000;
            pwszOut[iOut]     = (wchar_t)(0xD800 + (uiCode >> 10));
            pwszOut[iOut + 1] = (wchar_t)(0xDC00 + (uiCode & 0x3FF));
            iOut += 2;
        }
    }

    /* Terminate the converted text. */
    pwszOut[iOut] = L'\0';

    /* The caller owns and frees the converted text. */
    return(pwszOut);
}

/*----------------------------------------------------------------------------*/
/* BFU_WideToUtf8:                                                            */
/*                                                                            */
/* Convert a UTF-16 string to UTF-8.  NTFS does allow a file name to hold an  */
/* unpaired surrogate; such a name has no UTF-8 spelling, so the conversion   */
/* REFUSES it (NULL) and the caller reports the file as unrepresentable.      */
/*                                                                            */
/* Every code point is written in its shortest form, so the result is always  */
/* valid UTF-8 that BFU_Utf8ToWide would accept and turn back unchanged.      */
/*                                                                            */
/* As with BFU_Utf8ToWide, NULL also means out of memory, and *pbNoMemory     */
/* tells the two apart, so a caller reports an allocation failure as an       */
/* error and only a real unpaired surrogate as an unrepresentable name.       */
/*                                                                            */
/* Arguments:                                                                 */
/*     pwszWide   : the NUL-terminated UTF-16 text.                           */
/*     pbNoMemory : receives TRUE when NULL is returned because memory ran    */
/*                  out, FALSE otherwise (an unpaired surrogate, or success). */
/*                                                                            */
/* Returns:                                                                   */
/*     char * : the malloc'd UTF-8 text, or NULL on an unpaired surrogate or  */
/*              when out of memory (*pbNoMemory says which).                  */
/*----------------------------------------------------------------------------*/
char *BFU_WideToUtf8(const wchar_t *pwszWide, int *pbNoMemory)
{
    unsigned char *pbOut;
    size_t         cchIn;
    size_t         iIn;
    size_t         iOut;
    uint32_t       uiCode;
    uint32_t       uiLow;

    /* Measure the input, in UTF-16 units. */
    cchIn = wcslen(pwszWide);

    /* Not out of memory unless the allocation below fails: every other   */
    /* NULL return is an unpaired surrogate.                              */
    *pbNoMemory = FALSE;

    /* One UTF-16 unit becomes at most three UTF-8 bytes (a surrogate PAIR is   */
    /* two units and four bytes, which is fewer per unit), so 3 per unit plus   */
    /* the NUL is always enough room.                                           */
    pbOut = (unsigned char *)malloc(cchIn * 3 + 1);

    if (pbOut == NULL)
    {   /* Out of memory: said so, apart from an unpaired surrogate. */
        *pbNoMemory = TRUE;
        return(NULL);
    }

    /* Both cursors start at the beginning: iIn in units, iOut in bytes. */
    iIn  = 0;
    iOut = 0;

    while (iIn < cchIn)
    {   /* One code point per iteration. */

        /* Take one unit as an unsigned 16-bit value, so no wchar_t        */
        /* signedness or width can carry stray high bits into the tests.   */
        uiCode = (uint32_t)(uint16_t)pwszWide[iIn];
        iIn++;

        if (uiCode >= 0xD800 && uiCode <= 0xDBFF)
        {   /* A high surrogate: it is only legal with a low one right after. */
            if (iIn >= cchIn)
            {   /* The string ends on a lone high surrogate. */

                /* Refuse: free the half-converted output, return NULL. */
                free(pbOut);
                return(NULL);
            }

            uiLow = (uint32_t)(uint16_t)pwszWide[iIn];

            if (uiLow < 0xDC00 || uiLow > 0xDFFF)
            {   /* What follows is not a low surrogate. */

                /* Refuse: free the half-converted output, return NULL. */
                free(pbOut);
                return(NULL);
            }

            /* Consume the low half and combine the pair: 10 bits from each,   */
            /* above the 0x10000 the encoding subtracted.                      */
            iIn++;
            uiCode = 0x10000 + ((uiCode - 0xD800) << 10) + (uiLow - 0xDC00);
        }
        else if (uiCode >= 0xDC00 && uiCode <= 0xDFFF)
        {   /* A low surrogate with no high one before it. */

            /* Refuse: free the half-converted output and return NULL. */
            free(pbOut);
            return(NULL);
        }

        /* Emit the code point in the shortest UTF-8 form. */
        if (uiCode < 0x80)
        {   /* One byte: 0xxxxxxx. */
            pbOut[iOut] = (unsigned char)uiCode;
            iOut += 1;
        }
        else if (uiCode < 0x800)
        {   /* Two bytes: 110xxxxx 10xxxxxx. */
            pbOut[iOut]     = (unsigned char)(0xC0 | (uiCode >> 6));
            pbOut[iOut + 1] = (unsigned char)(0x80 | (uiCode & 0x3F));
            iOut += 2;
        }
        else if (uiCode < 0x10000)
        {   /* Three bytes: 1110xxxx 10xxxxxx 10xxxxxx. */
            pbOut[iOut]     = (unsigned char)(0xE0 | (uiCode >> 12));
            pbOut[iOut + 1] = (unsigned char)(0x80 | ((uiCode >> 6) & 0x3F));
            pbOut[iOut + 2] = (unsigned char)(0x80 | (uiCode & 0x3F));
            iOut += 3;
        }
        else
        {   /* Four bytes: 11110xxx 10xxxxxx 10xxxxxx 10xxxxxx. */
            pbOut[iOut]     = (unsigned char)(0xF0 | (uiCode >> 18));
            pbOut[iOut + 1] = (unsigned char)(0x80 | ((uiCode >> 12) & 0x3F));
            pbOut[iOut + 2] = (unsigned char)(0x80 | ((uiCode >> 6) & 0x3F));
            pbOut[iOut + 3] = (unsigned char)(0x80 | (uiCode & 0x3F));
            iOut += 4;
        }
    }

    /* Terminate the converted text. */
    pbOut[iOut] = '\0';

    /* The caller owns and frees the converted text. */
    return((char *)pbOut);
}

/*----------------------------------------------------------------------------*/
/* BFU_HostNameValid:                                                         */
/*                                                                            */
/* Decide whether an ext4 entry name can be created as a Windows file name.   */
/* ext4 forbids only '/' and NUL in a name; Windows forbids far more, and a   */
/* name it forbids either fails to create or -- worse -- silently creates     */
/* something else ("aux.txt" opens a device; "name." loses its dot and lands  */
/* on "name").  So an extract tests every name first and SKIPS what cannot    */
/* cross, instead of letting the host decide what the name means.             */
/*                                                                            */
/* Refused:                                                                   */
/*   - an empty name;                                                         */
/*   - any control character (below 0x20);                                    */
/*   - any of  < > : " / \ | ? *                                              */
/*   - a name ending in a dot or a space;                                     */
/*   - a reserved device name, with or without an extension: CON, PRN, AUX,   */
/*     NUL, COM1 to COM9, LPT1 to LPT9, and COM or LPT followed by a          */
/*     superscript one, two or three (U+00B9, U+00B2, U+00B3 -- Windows takes */
/*     them as the digits 1 to 3 in a device name), in any letter case.       */
/*                                                                            */
/* That is the list Microsoft's file-naming rules give.  COM0 and LPT0 are    */
/* not on it, so they are left as ordinary names.                             */
/*                                                                            */
/* The name is NOT checked for valid UTF-8 here; the caller converts it       */
/* afterwards and the conversion refuses what is malformed.                   */
/*                                                                            */
/* Used twice by an extract (bfe_copy.c): on every child's ext4 name before   */
/* it is created, and on the destination's own leaf name -- the last part of  */
/* the host path the caller named, converted to UTF-8 for the test.           */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszName : the entry name, UTF-8.                                       */
/*                                                                            */
/* Returns:                                                                   */
/*     int : TRUE when Windows can hold the name as it is, FALSE otherwise.   */
/*----------------------------------------------------------------------------*/
int BFU_HostNameValid(const char *pszName)
{
    static const char *apszReserved[] = {   // device names Windows reserves in every folder, upper case (the base is folded to match)
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };
    char   szBase[8];
    size_t cbName;
    size_t cbBase;
    size_t i;
    char   ch;

    /* Measure the name, in bytes. */
    cbName = strlen(pszName);

    if (cbName == 0)
    {   /* An empty name names nothing. */
        return(FALSE);
    }

    /* Characters Windows never allows in a file name, tested on the UTF-8    */
    /* BYTES -- sound for every test here: each character tested for is       */
    /* ASCII, and in UTF-8 no byte of a multi-byte character is below 0x80,   */
    /* so none can be mistaken for one.                                       */
    for (i = 0; i < cbName; i++)
    {   /* One byte of the name per iteration. */
        ch = pszName[i];

        if ((unsigned char)ch < 0x20)
        {   /* A control character. */
            return(FALSE);
        }

        if (ch == '<' || ch == '>' || ch == ':' || ch == '"' || ch == '/' ||
            ch == '\\' || ch == '|' || ch == '?' || ch == '*')
        {   /* A character with a meaning of its own in a Windows path. */
            return(FALSE);
        }
    }

    if (pszName[cbName - 1] == '.' || pszName[cbName - 1] == ' ')
    {   /* Windows strips a trailing dot or space, landing on another name.   */
        /* This also refuses "." and "..", which are never real children.     */
        return(FALSE);
    }

    /* The reserved-name test looks at the part before the FIRST dot only:       */
    /* "nul.tar.gz" is as reserved as "nul".  No reserved name is longer than    */
    /* four characters -- five bytes for the superscript forms, whose last       */
    /* character is two bytes of UTF-8 -- so a longer base cannot match and is   */
    /* not copied.                                                               */
    cbBase = 0;

    while (cbBase < cbName && pszName[cbBase] != '.')
    {   /* One character of the base name per iteration. */
        cbBase++;
    }

    if (cbBase >= 3 && cbBase <= 4)
    {   /* The right length to be a device name: upper-case it and compare.   */
        /* szBase holds at most four characters and the NUL.                  */
        for (i = 0; i < cbBase; i++)
        {   /* One character per iteration; only ASCII letters are folded. */
            ch = pszName[i];

            if (ch >= 'a' && ch <= 'z')
            {   /* Lower-case ASCII: fold to upper. */
                ch = (char)(ch - 'a' + 'A');
            }

            szBase[i] = ch;
        }

        /* Terminate the folded base for the comparison. */
        szBase[cbBase] = '\0';

        for (i = 0; i < sizeof(apszReserved) / sizeof(apszReserved[0]); i++)
        {   /* One reserved name per iteration. */
            if (strcmp(szBase, apszReserved[i]) == 0)
            {   /* The base is a device name. */
                return(FALSE);
            }
        }
    }

    if (cbBase == 5 && (unsigned char)pszName[3] == 0xC2 &&
        ((unsigned char)pszName[4] == 0xB9 || (unsigned char)pszName[4] == 0xB2 ||
         (unsigned char)pszName[4] == 0xB3))
    {   /* Three bytes, then a superscript one, two or three: U+00B9,    */
        /* U+00B2 and U+00B3, which are C2 B9, C2 B2 and C2 B3 in        */
        /* UTF-8.  Windows takes them as the digits 1 to 3 in a device   */
        /* name, so COM or LPT before one is a port; the three bytes     */
        /* are upper-cased as above and compared with the two words.     */
        for (i = 0; i < 3; i++)
        {   /* One of the three leading bytes per iteration; only ASCII   */
            /* letters are folded.                                        */
            ch = pszName[i];

            if (ch >= 'a' && ch <= 'z')
            {   /* Lower-case ASCII: fold to upper. */
                ch = (char)(ch - 'a' + 'A');
            }

            szBase[i] = ch;
        }

        /* Terminate the folded word for the comparison. */
        szBase[3] = '\0';

        if (strcmp(szBase, "COM") == 0 || strcmp(szBase, "LPT") == 0)
        {   /* COM or LPT with a superscript digit: a device name. */
            return(FALSE);
        }
    }

    /* Nothing Windows objects to. */
    return(TRUE);
}

/*----------------------------------------------------------------------------*/
/* BFU_LastComponent:                                                         */
/*                                                                            */
/* Find the final component of an image path -- the name an entry is listed   */
/* under.  The root has no component after its slash, so it is reported as    */
/* "/" itself.                                                                */
/*                                                                            */
/* Arguments:                                                                 */
/*     pszImagePath : an absolute image path.                                 */
/*                                                                            */
/* Returns:                                                                   */
/*     const char * : a pointer INTO pszImagePath (nothing to free).          */
/*----------------------------------------------------------------------------*/
const char *BFU_LastComponent(const char *pszImagePath)
{
    const char *pszSlash;

    /* The last slash in the path. */
    pszSlash = strrchr(pszImagePath, '/');

    if (pszSlash == NULL || pszSlash[1] == '\0')
    {   /* No slash at all, or nothing after the last one: the whole path.    */
        /* The test is "nothing after the last slash", not "the path is /",   */
        /* but image paths reach here without a trailing slash, so in         */
        /* practice that is only ever the root.                               */
        return(pszImagePath);
    }

    /* The text after the last slash. */
    return(pszSlash + 1);
}
