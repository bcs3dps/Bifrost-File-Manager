/*----------------------------------------------------------------------------*/
/* CommandSequences.cs                                                        */
/*                                                                            */
/* SPDX-License-Identifier: GPL-2.0-or-later                                  */
/* Copyright (c) 2026 B. C. Services                                          */
/*                                                                            */
/* The command worker's gate and its whole sequences.  Every request the      */
/* worker takes from the queue is first held to the program's present state   */
/* (Sys_Denied: is a filesystem open, which window owns the disk, is this     */
/* the same open as when it was asked), then run start to finish as one       */
/* sequence on the worker -- asking, closing editors, closing and opening     */
/* the session, opening the check and partition windows, Exit.  Only this     */
/* thread changes the mode, the target, write access and which editors are    */
/* registered; the windows only post requests and answer questions.           */
/*----------------------------------------------------------------------------*/

/* The framework: the base types, Enum.IsDefined, String.Equals and the    */
/* exceptions caught (System), the editor table's enumerator               */
/* (Collections.Generic), and the length and name of a disk image chosen   */
/* to partition (IO's FileInfo and Path).  No window or control is         */
/* touched here.                                                           */
using System;
using System.Collections.Generic;
using System.IO;

namespace BifrostFM
{

    internal sealed partial class SYS_clsSystem
    {
        /*--------------------------------------------------------------------*/
        /* Sys_Denied:                                                        */
        /*                                                                    */
        /* The gate: whether the program's present state lets this request    */
        /* run at all, decided on the worker when the request is taken from   */
        /* the queue -- not from what the window showed when it was posted,   */
        /* which may be out of date by then.  It answers a refusal's words,   */
        /* or "" to let the sequence start; each sequence still checks its    */
        /* own steps as it goes.  No engine call is made here.                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest : the request taken from the queue.                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     string : "" to run it; otherwise why it is refused, worded     */
        /*              for the user.                                         */
        /*--------------------------------------------------------------------*/
        private string Sys_Denied(RQT_clsRequest objRequest)
        {
            CCA_clsCopyArgs objCopy;
            RWA_clsRawArgs objRaw;
            INA_clsInputArgs objInput;

            /* Requests that need no open filesystem and no window's           */
            /* ownership pass at once: the notifications (a window has         */
            /* closed, an editor is ready -- bookkeeping, harmless even from   */
            /* a window long gone), Cancel and Exit, the two options, the      */
            /* device list and table read, and an image file's facts.  Their   */
            /* sequences have their own rules.                                 */
            if (SYS_Policy(objRequest.RQT_enmKind) == PRY_enmPriority.Notification ||
                objRequest.RQT_enmKind == KND_enmKind.Cancel || objRequest.RQT_enmKind == KND_enmKind.Exit ||
                objRequest.RQT_enmKind == KND_enmKind.SetVerify || objRequest.RQT_enmKind == KND_enmKind.SetRiskWarnings ||
                objRequest.RQT_enmKind == KND_enmKind.ListDevices || objRequest.RQT_enmKind == KND_enmKind.ReadDeviceTable ||
                objRequest.RQT_enmKind == KND_enmKind.ImageInfo)
            {   /* One of those: let it run. */
                return("");
            }

            /* The inputs (a picker, a value dialog) come next, each by its   */
            /* purpose, before the general rule further down that a Windows   */
            /* input needs nothing open.                                      */
            objInput = objRequest.RQT_objPayload as INA_clsInputArgs;
            if (objRequest.RQT_enmKind == KND_enmKind.OpenInput && objInput != null &&
                objInput.INA_enmKind == INK_enmInputKind.HostPaneAction)
            {   /* A pane action (the action is the request's flags): Windows   */
                /* work done in the window, needing no open image, but          */
                /* registered and waited for like any other input.              */
                if (objRequest.RQT_lOrigin != 0 || Sys_enmMode == MOD_enmMode.Partition ||
                    !Enum.IsDefined(typeof(HPA_enmHostAction), (HPA_enmHostAction)objRequest.RQT_uiFlags))
                {   /* From a window other than the main one, while the         */
                    /* partition window (modal over the main window) is open,   */
                    /* or with an action number no HPA_enmHostAction names:     */
                    /* refused.                                                 */
                    return("This pane action is not available in the current operation window.");
                }

                if ((HPA_enmHostAction)objRequest.RQT_uiFlags == HPA_enmHostAction.ImageCopySelection ||
                    (HPA_enmHostAction)objRequest.RQT_uiFlags == HPA_enmHostAction.ImageCutSelection)
                {   /* Copy or Cut of image items to the clipboard: the paths   */
                    /* name entries of the filesystem open when the click was   */
                    /* made, so that same open must still be the open one --    */
                    /* same epoch, same range.                                  */
                    if (Sys_enmMode != MOD_enmMode.Open || objRequest.RQT_lEpoch != Sys_lEpoch ||
                        Sys_objTarget == null || !Sys_objTarget.TGT_SameRange(objRequest.RQT_objTarget) ||
                        ((HPA_enmHostAction)objRequest.RQT_uiFlags == HPA_enmHostAction.ImageCutSelection && !Sys_bWritable))
                    {   /* Not that open any more, or a Cut while the session   */
                        /* is read-only (a cut removes its sources later):      */
                        /* refused, so nothing usable reaches the clipboard.    */
                        return("That clipboard selection no longer belongs to a writable or readable open filesystem.");
                    }
                }

                /* Let it run.  Whether other work overlaps it was settled   */
                /* when it was posted; this gate checks only the owner and   */
                /* the source as they are now.                               */
                return("");
            }

            if (objRequest.RQT_enmKind == KND_enmKind.OpenInput && objInput != null &&
                (objInput.INA_enmKind == INK_enmInputKind.ImageNewFile ||
                objInput.INA_enmKind == INK_enmInputKind.HostNewFile ||
                objInput.INA_enmKind == INK_enmInputKind.HostOpenEntry))
            {   /* New File (in the image or in Windows) or opening a Windows   */
                /* file in an editor: the file's own input, before anything     */
                /* is made or opened.  The partition window is modal over the   */
                /* main window and refuses them; the check window is not, and   */
                /* does not.                                                    */
                if (objRequest.RQT_lOrigin != 0 || Sys_enmMode == MOD_enmMode.Partition)
                {   /* From another window, or the partition window open:   */
                    /* refused before any dialog is shown.                  */
                    return("Close the partition window before opening or creating a file.");
                }

                if ((objInput.INA_enmKind == INK_enmInputKind.HostOpenEntry && objRequest.RQT_lstPaths.Count != 1) ||
                    (objInput.INA_enmKind != INK_enmInputKind.HostOpenEntry && objRequest.RQT_lstPaths.Count != 0))
                {   /* Open names exactly one Windows file; New names none   */
                    /* (its name comes later, typed into the folder that     */
                    /* was captured).  Any other count: refused.             */
                    return("This file input has an invalid captured selection.");
                }

                if (objInput.INA_enmKind == INK_enmInputKind.ImageNewFile &&
                    (Sys_enmMode != MOD_enmMode.Open || objRequest.RQT_lEpoch != Sys_lEpoch ||
                    Sys_objTarget == null || !Sys_objTarget.TGT_SameRange(objRequest.RQT_objTarget)))
                {   /* A new file in the image, but the filesystem it was      */
                    /* asked for is no longer the open one (another epoch or   */
                    /* range): refused, so the file is never made in a         */
                    /* different filesystem.                                   */
                    return("The filesystem selected for the new file is no longer open.");
                }

                /* Let it run.  A Windows file needs no open session; the    */
                /* image's new file keeps this source, and the create that   */
                /* follows is gated again.                                   */
                return("");
            }

            if (objRequest.RQT_enmKind == KND_enmKind.OpenInput && objInput != null &&
                (objInput.INA_enmKind == INK_enmInputKind.EntryProperties ||
                objInput.INA_enmKind == INK_enmInputKind.FilesystemProperties))
            {   /* Properties of an entry or of the filesystem: they belong   */
                /* to the filesystem open when the click was made, whatever   */
                /* the pane shows now -- even another partition of the same   */
                /* file.                                                      */
                if (objRequest.RQT_lOrigin != 0 || Sys_enmMode != MOD_enmMode.Open ||
                    objRequest.RQT_lEpoch != Sys_lEpoch || Sys_objTarget == null ||
                    !Sys_objTarget.TGT_SameRange(objRequest.RQT_objTarget))
                {   /* Not from the main window, or that open is gone:   */
                    /* refused before the engine is asked anything and   */
                    /* before the dialog is registered.                  */
                    return("The filesystem selected for Properties is no longer open.");
                }

                if (objInput.INA_enmKind == INK_enmInputKind.EntryProperties &&
                    (objInput.INA_objEntryProperties == null || objRequest.RQT_lstPaths.Count != 1))
                {   /* An entry's Properties without its one path or the    */
                    /* listing's values for it (a malformed request, made   */
                    /* inside the program): refused.                        */
                    return("Entry Properties needs one captured entry path and listing value.");
                }

                if (objInput.INA_enmKind == INK_enmInputKind.FilesystemProperties && objRequest.RQT_lstPaths.Count != 0)
                {   /* The filesystem's Properties names no entry; one that   */
                    /* does is malformed and refused.                         */
                    return("Filesystem Properties takes no entry paths.");
                }

                /* Let it run: showing properties needs read access only; an     */
                /* edit of the attributes is a later request of its own, gated   */
                /* again and the one that may ask for write access.              */
                return("");
            }

            if (objRequest.RQT_enmKind == KND_enmKind.OpenInput && objRequest.RQT_objPayload is INA_clsInputArgs &&
                (((INA_clsInputArgs)objRequest.RQT_objPayload).INA_enmKind == INK_enmInputKind.HostOpenImageFile ||
                ((INA_clsInputArgs)objRequest.RQT_objPayload).INA_enmKind == INK_enmInputKind.HostCheckImageFile))
            {   /* The main window's image-file pickers (Open, Check): they     */
                /* need no open session, but not while the check or partition   */
                /* window owns a disk; and Check's picker only with nothing     */
                /* open at all.  (Write Device asks its image in its own        */
                /* request's sequence, gated as the WriteDevice kind below.)    */
                if (objRequest.RQT_lOrigin != 0 || Sys_enmMode == MOD_enmMode.Check || Sys_enmMode == MOD_enmMode.Partition ||
                    (((INA_clsInputArgs)objRequest.RQT_objPayload).INA_enmKind == INK_enmInputKind.HostCheckImageFile && Sys_enmMode != MOD_enmMode.None))
                {   /* One of those windows open, or Check's picker with a   */
                    /* filesystem open (checking the open one is its own     */
                    /* request): refused before the picker is shown.         */
                    return("Close the active filesystem or operation window before selecting that image file.");
                }

                /* Let it run: the picker only collects a name; the request   */
                /* it leads to is gated again.                                */
                return("");
            }

            if (objRequest.RQT_enmKind == KND_enmKind.OpenInput && objRequest.RQT_objPayload is INA_clsInputArgs &&
                !((INA_clsInputArgs)objRequest.RQT_objPayload).INA_IsImage())
            {   /* Any other input that is not about the image: it only   */
                /* collects values and touches no session, so it runs.    */
                return("");
            }

            if (objRequest.RQT_enmKind == KND_enmKind.ViewerFallback && objRequest.RQT_objPayload is VFA_clsViewerFallbackArgs &&
                ((VFA_clsViewerFallbackArgs)objRequest.RQT_objPayload).VFA_bHost)
            {   /* The text-editor fallback for a WINDOWS file: no image      */
                /* session is needed.  Its sequence still asks the question   */
                /* and opens that Windows file, never an image path.          */
                return("");
            }

            /* The clipboard's image items are tied to the open that copied     */
            /* them: each copy carries the clipboard lifetime it was made in,   */
            /* stepped at every close (Sys_CloseSession), so a request posted   */
            /* by a window that has not yet seen the close is still caught.     */
            objCopy = objRequest.RQT_objPayload as CCA_clsCopyArgs;
            if (objCopy != null && objCopy.CCA_lClipboardLifetime >= 0 &&
                objCopy.CCA_lClipboardLifetime != Sys_lClipboardLifetime)
            {   /* Copied in an earlier lifetime: the paths may name another   */
                /* filesystem's entries now, so refused before any copy.       */
                return("The filesystem has closed since these items were copied to the clipboard. Copy them again.");
            }

            if ((objRequest.RQT_enmKind == KND_enmKind.Extract || objRequest.RQT_enmKind == KND_enmKind.Import ||
                objRequest.RQT_enmKind == KND_enmKind.CopyWithin) && objCopy != null && objCopy.CCA_bMoveSources &&
                objCopy.CCA_uiClipboard != 0 && Sys_setConsumedCuts.Contains(objCopy.CCA_uiClipboard))
            {   /* A paste of a cut that has already been pasted once (its   */
                /* clipboard number is in the spent set): refused, so the    */
                /* move -- and its removal of the sources -- never runs      */
                /* twice.                                                    */
                return("That cut has already been consumed. Cut the source again to request another move.");
            }

            switch (objRequest.RQT_enmKind)
            {   /* The rest, by kind: which mode and which window each   */
                /* needs.                                                */
                case KND_enmKind.OpenDevice:
                    /* Open Device / Check a device, after its picker: the   */
                    /* request must name a whole physical disk.              */
                    if (!(objRequest.RQT_objPayload is DOA_clsDeviceOpenArgs))
                    {   /* Not a device's arguments: malformed, refused. */
                        return("No complete physical-device selection was supplied.");
                    }

                    if (!((DOA_clsDeviceOpenArgs)objRequest.RQT_objPayload).DOA_bAsk &&
                        (objRequest.RQT_objTarget == null ||
                        objRequest.RQT_objTarget.TGT_ullOffset != 0 || objRequest.RQT_objTarget.TGT_ullLength != 0 ||
                        objRequest.RQT_objTarget.TGT_ullDiskBytes == 0 ||
                        !objRequest.RQT_objTarget.TGT_sPath.StartsWith(DEV_clsDisk.DEV_PHYSICAL_PREFIX, StringComparison.OrdinalIgnoreCase)))
                    {   /* A device already chosen, but a range rather than the   */
                        /* whole disk, no size, or not a physical-drive path:     */
                        /* a malformed request, refused.  A device still to be    */
                        /* asked has its target tested by the sequence once it    */
                        /* is chosen (Sys_DeviceOpenSequence).                    */
                        return("No complete physical-device selection was supplied.");
                    }

                    if (Sys_enmMode == MOD_enmMode.Partition || Sys_enmMode == MOD_enmMode.Check ||
                        (((DOA_clsDeviceOpenArgs)objRequest.RQT_objPayload).DOA_bCheck && Sys_enmMode != MOD_enmMode.None))
                    {   /* The check or partition window owns a disk, or this   */
                        /* is a Check of a device while a filesystem is open    */
                        /* (checking the open one is the Check request's own    */
                        /* job): refused.                                       */
                        return("Close the active operation window before changing targets; select a check device only with no filesystem open.");
                    }

                    /* Let it run; its sequence checks each step. */
                    return("");
                case KND_enmKind.OpenImageFile:
                    /* Open / Check an image file, after its picker: the   */
                    /* request must name one whole Windows file.           */
                    if (!(objRequest.RQT_objPayload is IFA_clsImageFileArgs) || objRequest.RQT_objTarget == null ||
                        objRequest.RQT_objTarget.TGT_ullOffset != 0 || objRequest.RQT_objTarget.TGT_ullLength != 0 ||
                        objRequest.RQT_objTarget.TGT_sDeviceText.Length != 0 ||
                        objRequest.RQT_objTarget.TGT_sPath.StartsWith("\\\\.\\", StringComparison.Ordinal))
                    {   /* Not a file's arguments, a range, a device's         */
                        /* description or a "\\.\" path: malformed, refused.   */
                        return("No complete image-file selection was supplied.");
                    }

                    if (Sys_enmMode == MOD_enmMode.Partition || Sys_enmMode == MOD_enmMode.Check ||
                        (((IFA_clsImageFileArgs)objRequest.RQT_objPayload).IFA_bCheck && Sys_enmMode != MOD_enmMode.None))
                    {   /* As for a device: an owning window open, or a Check   */
                        /* of a file while a filesystem is open: refused.       */
                        return("Close the active operation window before changing targets; select a check file only with no filesystem open.");
                    }

                    /* Let it run; its sequence checks each step. */
                    return("");
                case KND_enmKind.Open:
                case KND_enmKind.NewImage:
                    /* Opening a filesystem, or making a new image: refused    */
                    /* only while the check or partition window owns a disk;   */
                    /* an open filesystem is closed by the sequence, after     */
                    /* asking.                                                 */
                    if (Sys_enmMode == MOD_enmMode.Partition || Sys_enmMode == MOD_enmMode.Check)
                    {   /* One of those windows open -- even a check window   */
                        /* that has finished still owns its target.           */
                        return("Close the partition/check window before changing targets.");
                    }

                    /* Let it run; its sequence checks each step. */
                    return("");
                case KND_enmKind.Check:
                    /* Check Filesystem: the open one, or a file or device   */
                    /* chosen with nothing open.                             */
                    if (Sys_enmMode == MOD_enmMode.Partition || Sys_enmMode == MOD_enmMode.Check)
                    {   /* A check or partition window owns a disk, idle or   */
                        /* not: refused.                                      */
                        return("Close the partition/check window before checking another target.");
                    }

                    if (Sys_enmMode == MOD_enmMode.Open && (objRequest.RQT_lEpoch != Sys_lEpoch ||
                        Sys_objTarget == null || !Sys_objTarget.TGT_SameRange(objRequest.RQT_objTarget)))
                    {   /* A filesystem is open, but not the one this check   */
                        /* was asked for (another epoch or range): refused,   */
                        /* rather than checking a different filesystem.       */
                        return("The open filesystem changed after this check was requested.");
                    }

                    if (Sys_enmMode == MOD_enmMode.Open && (Sys_uiVolumeType == ENG_clsNative.ENG_VOLTYPE_FAT12 ||
                        Sys_uiVolumeType == ENG_clsNative.ENG_VOLTYPE_FAT16 ||
                        Sys_uiVolumeType == ENG_clsNative.ENG_VOLTYPE_FAT32 ||
                        Sys_uiVolumeType == ENG_clsNative.ENG_VOLTYPE_EXFAT))
                    {   /* The open filesystem is FAT or exFAT: refused here,     */
                        /* before the risk question and before the session is     */
                        /* closed for the check.  The check is e2fsprogs'         */
                        /* e2fsck, which knows only ext2, ext3 and ext4, so       */
                        /* going on would close the volume, have e2fsck fail to   */
                        /* open it, and reopen it as a new epoch -- its editors   */
                        /* asked to close for nothing.  The window greys Check    */
                        /* for such a volume (MainCommand.cs); this is the        */
                        /* worker's own test, which a request posted before the   */
                        /* open's facts reached the window still meets.  A FAT    */
                        /* volume picked with nothing open is left to e2fsck to   */
                        /* refuse, as nothing is closed for it.                   */
                        return("Check Filesystem runs e2fsck, which checks only ext2, ext3 and ext4 filesystems; the open filesystem is FAT or exFAT.");
                    }

                    if (Sys_enmMode == MOD_enmMode.Open && Sys_uiVolumeType == ENG_clsNative.ENG_VOLTYPE_SQUASHFS)
                    {   /* The open filesystem is squashfs: refused here   */
                        /* for the FAT test's reasons -- e2fsck cannot     */
                        /* read it, and going on would close it and its    */
                        /* editors for nothing.  The window greys Check    */
                        /* for it too (MainCommand.cs).                    */
                        return("Check Filesystem runs e2fsck, which checks only ext2, ext3 and ext4 filesystems; the open filesystem is squashfs.");
                    }

                    /* Let it run; its sequence checks each step. */
                    return("");
                case KND_enmKind.OpenPartition:
                    /* The partition window: refused only while the check or    */
                    /* partition window owns a disk.  An open filesystem, and   */
                    /* the editors of one -- or editors left from a closed      */
                    /* one -- do not refuse it: its sequence asks to close      */
                    /* the filesystem, each editor asked as Exit asks it,       */
                    /* once the disk has been chosen.                           */
                    if (Sys_enmMode == MOD_enmMode.Partition || Sys_enmMode == MOD_enmMode.Check)
                    {   /* One of those windows open -- even a check window   */
                        /* that has finished still owns its target.           */
                        return("Close the partition/check window before partitioning.");
                    }

                    /* Let it run; its sequence checks each step. */
                    return("");
                case KND_enmKind.Close:
                    /* Close Image always runs: even with nothing open it   */
                    /* settles editors left over from a failed reopen.      */
                    return("");
                case KND_enmKind.ClosePartition:
                case KND_enmKind.OpenPartitionEntry:
                    /* The partition window's Close, or its Open of one       */
                    /* partition (which closes the window first): only from   */
                    /* the window that owns the disk now.                     */
                    if (Sys_enmMode != MOD_enmMode.Partition || objRequest.RQT_lOrigin != Sys_lModeWindow ||
                        Sys_objTarget == null || objRequest.RQT_objTarget == null ||
                        !String.Equals(Sys_objTarget.TGT_sPath, objRequest.RQT_objTarget.TGT_sPath,
                        StringComparison.OrdinalIgnoreCase))
                    {   /* An older window's request, or another disk:          */
                        /* refused, so it cannot close a newer window or open   */
                        /* a partition of another disk.                         */
                        return("This partition window no longer owns that disk.");
                    }

                    if (objRequest.RQT_enmKind == KND_enmKind.OpenPartitionEntry &&
                        (objRequest.RQT_objTarget.TGT_ullOffset >= Sys_objTarget.TGT_ullDiskBytes ||
                        objRequest.RQT_objTarget.TGT_ullLength >
                        Sys_objTarget.TGT_ullDiskBytes - objRequest.RQT_objTarget.TGT_ullOffset))
                    {   /* The partition to open starts at or past the       */
                        /* disk's end, or runs past it (by subtraction, so   */
                        /* it cannot wrap; a length of 0, "to the end", is   */
                        /* allowed): refused BEFORE the window is closed,    */
                        /* which would otherwise close for nothing.          */
                        return("The selected filesystem range is outside this partition window's disk.");
                    }

                    /* Let it run: the window's close is asked and waited for   */
                    /* before anything is opened.                               */
                    return("");
                case KND_enmKind.Repair:
                    /* The check window's Repair: only from the check window   */
                    /* that owns the target now, and for that same target.     */
                    if (Sys_enmMode != MOD_enmMode.Check || objRequest.RQT_lOrigin != Sys_lModeWindow ||
                        Sys_objTarget == null || !Sys_objTarget.TGT_SameRange(objRequest.RQT_objTarget))
                    {   /* An older window's Repair, or another target:         */
                        /* refused -- a repair must never run on a filesystem   */
                        /* chosen since.                                        */
                        return("This check window no longer owns that target.");
                    }

                    /* Let it run; its sequence checks each step. */
                    return("");
                case KND_enmKind.ReadLayout:
                case KND_enmKind.WriteTable:
                case KND_enmKind.MakeFilesystem:
                case KND_enmKind.ExpandFilesystem:
                case KND_enmKind.TestCapacity:
                case KND_enmKind.ReadPartition:
                case KND_enmKind.WritePartition:
                case KND_enmKind.ErasePartition:
                case KND_enmKind.EditTable:
                    /* The partition window's work on its disk: only from the   */
                    /* window that owns the disk now.  The range may be any     */
                    /* part of the disk; the disk itself may not differ.        */
                    if (Sys_enmMode != MOD_enmMode.Partition || objRequest.RQT_lOrigin != Sys_lModeWindow ||
                        Sys_objTarget == null || objRequest.RQT_objTarget == null ||
                        !String.Equals(Sys_objTarget.TGT_sPath, objRequest.RQT_objTarget.TGT_sPath,
                        StringComparison.OrdinalIgnoreCase))
                    {   /* An older window's request, or another disk's path:   */
                        /* refused before anything is read or written.          */
                        return("This partition window no longer owns that disk.");
                    }

                    if ((objRequest.RQT_enmKind == KND_enmKind.ReadPartition ||
                        objRequest.RQT_enmKind == KND_enmKind.WritePartition ||
                        objRequest.RQT_enmKind == KND_enmKind.ErasePartition) &&
                        (objRequest.RQT_objTarget.TGT_ullLength == 0 ||
                        objRequest.RQT_objTarget.TGT_ullOffset > Sys_objTarget.TGT_ullDiskBytes ||
                        objRequest.RQT_objTarget.TGT_ullLength >
                        Sys_objTarget.TGT_ullDiskBytes - objRequest.RQT_objTarget.TGT_ullOffset))
                    {   /* A partition's Read, Write or Erase whose range is   */
                        /* empty or not wholly on this window's disk (tested   */
                        /* by subtraction, so it cannot wrap): refused.  The   */
                        /* engine still holds every copy to the disk itself.   */
                        return("The selected partition is outside this partition window's disk.");
                    }

                    /* Let it run; its sequence checks each step. */
                    return("");
                case KND_enmKind.ReadDevice:
                case KND_enmKind.WriteDevice:
                    /* Read Device and Write Device: they need no open     */
                    /* filesystem, but must not write over the open one.   */
                    objRaw = objRequest.RQT_objPayload as RWA_clsRawArgs;
                    if (Sys_enmMode == MOD_enmMode.Open && Sys_objTarget != null && objRaw != null &&
                        String.Equals(Sys_objTarget.TGT_sPath, objRaw.RWA_sTo, StringComparison.OrdinalIgnoreCase))
                    {   /* The destination is the open filesystem's own file   */
                        /* or device, by its path: refused.  This test is      */
                        /* only by spelling; another spelling of the same      */
                        /* object is caught when the copy runs by the          */
                        /* engine's identity test (bfe_image.c,                */
                        /* BFD_TargetIsObject), which holds two whole disks    */
                        /* apart by number -- the only device paths this       */
                        /* program passes (a volume path whose disk Windows    */
                        /* will not name is the one form it would miss).       */
                        return("That destination is open in this window. Close it before raw imaging to it.");
                    }

                    if (objRequest.RQT_enmKind == KND_enmKind.WriteDevice && Sys_enmMode == MOD_enmMode.None && Sys_lstEditors.Count != 0)
                    {   /* Write Device while editors from a filesystem that   */
                        /* could not be reopened still hold unsaved work:      */
                        /* refused until they are settled, since the device    */
                        /* written may be the one they came from.              */
                        return("Close the editors from the unavailable filesystem before writing a device.");
                    }

                    if (Sys_enmMode == MOD_enmMode.Check || Sys_enmMode == MOD_enmMode.Partition)
                    {   /* The check or partition window owns a disk: refused,   */
                        /* so a raw copy never runs beside its work.             */
                        return("Close the check/partition window before raw imaging.");
                    }

                    /* Let it run; its sequence checks each step. */
                    return("");
                default:
                    /* Everything else -- listings, edits, copies, the         */
                    /* editors' reads and saves -- needs an open filesystem,   */
                    /* and the very one the request was made against.          */
                    if (Sys_enmMode != MOD_enmMode.Open)
                    {   /* Nothing open: refused. */
                        return("No filesystem is open for this command.");
                    }

                    if (objRequest.RQT_lEpoch != Sys_lEpoch || Sys_objTarget == null ||
                        !Sys_objTarget.TGT_SameRange(objRequest.RQT_objTarget))
                    {   /* Open, but another epoch or range than the request's   */
                        /* -- the same file name alone could be another          */
                        /* partition, or the file opened again: refused.         */
                        return("The filesystem this request belongs to is no longer open.");
                    }

                    /* The same open, the same range: let it run. */
                    return("");
            }
        }


        /*--------------------------------------------------------------------*/
        /* Sys_Do:                                                            */
        /*                                                                    */
        /* One step of a sequence -- an engine call through the step runner   */
        /* -- run only if the command has not been stopped: a Cancel, or an   */
        /* Exit agreed, keeps the next step from starting.  (A step already   */
        /* running, a copy say, is stopped through its own progress callback, */
        /* not here.)                                                         */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     enmStep      : the step.                                       */
        /*     objRequest   : its arguments.                                  */
        /*     objExecution : the running command's services.                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : the step's outcome, or CANCELLED when it was   */
        /*                     not started; the caller runs nothing further   */
        /*                     after a failure.                               */
        /*--------------------------------------------------------------------*/
        private RSL_clsResult Sys_Do(STP_enmStep enmStep, RQT_clsRequest objRequest, EXC_clsExecution objExecution)
        {
            if (!objExecution.EXC_Checkpoint())
            {   /* Stopped: the step is not started, so nothing more is   */
                /* written after a Cancel or an agreed Exit.              */
                return(new RSL_clsResult(SYS_CANCELLED, "Operation cancelled.", null));
            }

            /* The step, run here on the worker; the step runner calls the   */
            /* engine directly (CommandNativeSteps.cs).                      */
            return(Sys_objSteps.STB_Run(enmStep, objRequest, objExecution));
        }

        /*--------------------------------------------------------------------*/
        /* Sys_Yes:                                                           */
        /*                                                                    */
        /* A Yes / No question asked and waited for: true only for Yes.  No,  */
        /* Cancel, a window that has gone and a command stopped while it      */
        /* waited are all "no", so the caller goes no further and nothing     */
        /* changes.                                                           */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     enmKind    : the question's kind (its words are                */
        /*                  CommandQuestionPresenter.cs Qpr_Warning's).       */
        /*     objRequest : the request it is about.                          */
        /*     lWindow    : the window to ask over; 0 for the main window.    */
        /*                                                                    */
        /* Returns:                                                           */
        /*     bool : true for Yes only.                                      */
        /*--------------------------------------------------------------------*/
        private bool Sys_Yes(QNK_enmQuestionKind enmKind, RQT_clsRequest objRequest, long lWindow)
        {
            ANS_clsAnswer objAnswer;

            /* Asked, and waited for (SYS_Ask): the answer -- Cancel when   */
            /* it could not be shown, the command was stopped, or the       */
            /* command is a drag's extract, which asks nothing.             */
            objAnswer = SYS_Ask(enmKind, objRequest, lWindow);

            /* Yes only; anything else leaves the caller where it was.  The   */
            /* null test is a guard: SYS_Ask always answers.                  */
            return(objAnswer != null && objAnswer.ANS_enmKind == ANK_enmAnswerKind.Yes);
        }

        /*--------------------------------------------------------------------*/
        /* Sys_CloseSession:                                                  */
        /*                                                                    */
        /* Close the open session in the engine, and record that it is        */
        /* closed -- the mode, the target and write access cleared, the       */
        /* clipboard's lifetime stepped -- and publish the new state.  A      */
        /* close that fails (its final commit) still leaves the session       */
        /* closed, so the state is cleared either way; the failure is         */
        /* returned for the caller to stop on.                                */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest   : the request the close is part of.               */
        /*     objExecution : the running command's services.                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : the close's outcome, or CANCELLED (nothing     */
        /*                     closed) when the command was stopped first.    */
        /*--------------------------------------------------------------------*/
        private RSL_clsResult Sys_CloseSession(RQT_clsRequest objRequest, EXC_clsExecution objExecution)
        {
            RSL_clsResult objResult;

            if (!objExecution.EXC_Checkpoint())
            {   /* Stopped first: the engine is not called, so the session   */
                /* is still open and the state is left saying so.            */
                return(new RSL_clsResult(SYS_CANCELLED, "Close cancelled before it started.", null));
            }

            /* The close, called past Sys_Do (its checkpoint has just been    */
            /* passed).  BFE_Close releases the session even when its final   */
            /* commit fails, so the state below is cleared whatever the       */
            /* status, and the status goes back to the caller.                */
            objResult = Sys_objSteps.STB_Run(STP_enmStep.Close, objRequest, objExecution);

            /* The clipboard's image items die with the session -- even on a    */
            /* failed commit, and even for a reopen that changes only write     */
            /* access and keeps the editors' epoch.  Stepping the lifetime      */
            /* makes Sys_Denied refuse any later paste of them.  checked()      */
            /* turns an overflow into an exception rather than a wrap back to   */
            /* a lifetime already used.                                         */
            Sys_lClipboardLifetime = checked(Sys_lClipboardLifetime + 1);

            /* Nothing is open now -- no mode, no target, no write access and   */
            /* no filesystem type -- and the state published at once, so the    */
            /* windows stop offering the session's commands.                    */
            Sys_enmMode = MOD_enmMode.None;
            Sys_objTarget = null;
            Sys_bWritable = false;
            Sys_uiVolumeType = 0;
            Sys_PublishState(objRequest);

            /* The close's own outcome -- a failed commit stays a failure   */
            /* though the session has ended.                                */
            return(objResult);
        }

        /*--------------------------------------------------------------------*/
        /* Sys_OpenSession:                                                   */
        /*                                                                    */
        /* Open the request's target in the engine, read-only or writable,    */
        /* and only if that succeeds record it as the open filesystem and     */
        /* publish it.  A new open starts a new epoch (every request made     */
        /* against the old one is then refused); a reopen of the same target  */
        /* to change write access keeps the epoch, so the editors' work and   */
        /* the pane's folder carry over.                                      */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest   : the request whose target is opened.             */
        /*     objExecution : the running command's services.                 */
        /*     bWritable    : open for writing.                               */
        /*     bNewEpoch    : true for a new open, false for a write-access   */
        /*                    reopen of the same target.                      */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : the open's outcome; after a failure the state  */
        /*                     is left as it was (nothing open).              */
        /*--------------------------------------------------------------------*/
        private RSL_clsResult Sys_OpenSession(RQT_clsRequest objRequest, EXC_clsExecution objExecution,
            bool bWritable, bool bNewEpoch)
        {
            RSL_clsResult objResult;
            RQT_clsRequest objOpen;
            EOR_clsOpenResult objOpened;
            KND_enmKind enmOpen;

            /* The access asked of the engine is this function's argument,   */
            /* carried as the open request's kind -- never taken from the    */
            /* surrounding request's own flags.                              */
            enmOpen = KND_enmKind.MakeReadOnly;
            if (bWritable)
            {   /* Writable: only Sys_FirstChange's reopen asks for it,    */
                /* after a Yes to the first-change question (or with the   */
                /* risk warnings off).                                     */
                enmOpen = KND_enmKind.MakeWritable;
            }

            /* The open, as a request of its own made here (never queued,   */
            /* nothing of its own to release), on the same target.          */
            objOpen = new RQT_clsRequest(enmOpen,
                objRequest.RQT_lOrigin, objRequest.RQT_lEpoch, objRequest.RQT_objTarget,
                null, 0, 0, null, "", "");
            objResult = Sys_Do(STP_enmStep.Open, objOpen, objExecution);
            if (objResult.RSL_iStatus != SYS_OK)
            {   /* Not opened: the state still says nothing is open, so the   */
                /* gate refuses every session request, an editor's save       */
                /* included, until an open succeeds.                          */
                return(objResult);
            }

            /* Opened: recorded as the open filesystem, with its access. */
            Sys_enmMode = MOD_enmMode.Open;
            Sys_objTarget = objRequest.RQT_objTarget;
            Sys_bWritable = bWritable;

            /* And with its filesystem type, from the facts the open step read   */
            /* (an EOR_clsOpenResult, CommandNativeSteps.cs): the gate refuses   */
            /* Check Filesystem on a FAT or exFAT volume by it, since the        */
            /* window's greying of Check is only advice.  Every write-access     */
            /* reopen reads the facts again, so a reopen sets it afresh too.     */
            objOpened = objResult.RSL_objPayload as EOR_clsOpenResult;
            Sys_uiVolumeType = 0;
            if (objOpened != null)
            {   /* The open's record, which a successful open step always      */
                /* carries; without one the type stays 0, which names no FAT   */
                /* type, so nothing is refused for it.                         */
                Sys_uiVolumeType = objOpened.EOR_stVolume.VLI_uiVolumeType;
            }

            if (bNewEpoch)
            {   /* A new open -- even of the same file name: a new epoch, so   */
                /* any request or snapshot from the old one is refused, and    */
                /* the pane starts at the root, the old folder meaning         */
                /* nothing in a new open.  A write-access reopen skips this    */
                /* and keeps the folder, so the refresh after a first change   */
                /* does not jump back to the root.                             */
                Sys_lEpoch = checked(Sys_lEpoch + 1);
                Sys_sMainFolder = "/";
            }

            /* The state first, then the Opened view (its facts: the       */
            /* presenter's fast_commit note, the title), both before the   */
            /* caller's first listing.                                     */
            Sys_PublishState(objRequest);
            SYS_Show(new VWM_clsView(VWK_enmViewKind.Opened, objRequest, objResult));

            /* Opened; the caller may list it. */
            return(objResult);
        }

        /*--------------------------------------------------------------------*/
        /* Sys_RefuseReadOnlyFilesystem:                                      */
        /*                                                                    */
        /* Refuse a change to a filesystem nothing can write -- squashfs --   */
        /* with a notice the user must close (OK alone), so the refusal is    */
        /* never only a status line.  A change request runs this before any   */
        /* question of its own (a Delete's, a name, a copy's conflicts), so   */
        /* a squashfs session asks nothing about a change that cannot         */
        /* happen; and one request -- a drop of many files is one -- gets one */
        /* notice and is refused whole, before anything is copied.            */
        /* Sys_FirstChange runs it too, for the changes that reach write      */
        /* access with no question first (an editor's Save, New File).        */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest : the change being attempted; the notice shows over */
        /*                  the window it came from.                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : SYS_OK when the open filesystem may be         */
        /*                     written (or nothing marks it unwritable);      */
        /*                     SYS_REFUSED, after the notice, for squashfs.   */
        /*--------------------------------------------------------------------*/
        private RSL_clsResult Sys_RefuseReadOnlyFilesystem(RQT_clsRequest objRequest)
        {
            if (Sys_uiVolumeType != ENG_clsNative.ENG_VOLTYPE_SQUASHFS)
            {   /* Not squashfs: nothing refused here.  Whether the change   */
                /* may go ahead is still the caller's -- the risk question   */
                /* and the writable reopen come after.                       */
                return(new RSL_clsResult(SYS_OK, "", null));
            }

            /* The notice, over the window the request came from, and   */
            /* waited for; its answer means nothing (OK alone), so it   */
            /* is not read.  A drag's extract, which can ask nothing,   */
            /* never comes here: it changes nothing in the image.       */
            SYS_Ask(QNK_enmQuestionKind.ReadOnlyFilesystem, objRequest, objRequest.RQT_lOrigin);

            /* Refused: no risk question and no close -- a writable    */
            /* reopen would only be refused by the engine, after the   */
            /* editors had been asked about a close that changes       */
            /* nothing.  The reason also goes to the status line.      */
            return(new RSL_clsResult(SYS_REFUSED, "squashfs is a read-only filesystem: nothing in it can be changed.", null));
        }

        /*--------------------------------------------------------------------*/
        /* Sys_FirstChange:                                                   */
        /*                                                                    */
        /* Before the first change to a filesystem opened read-only: ask (the */
        /* risk warning, unless the warnings are off), then close it and      */
        /* reopen the same target writable.  The epoch is kept, so the        */
        /* editors stay registered and their work stays valid.  Every caller  */
        /* that changes the filesystem runs this first and goes on only on    */
        /* success.  A squashfs filesystem is refused here, with the          */
        /* read-only notice and no risk question: it cannot be written at     */
        /* all.                                                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest   : the change being attempted (its target is the   */
        /*                    open one -- the gate held it to that).          */
        /*     objExecution : the running command's services; no other        */
        /*                    request runs in between.                        */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : OK only when the session is now writable; a    */
        /*                     No, a failed close, or a failed writable open  */
        /*                     otherwise (after which a read-only reopen is   */
        /*                     tried, but the change still fails); REFUSED    */
        /*                     for squashfs.                                  */
        /*--------------------------------------------------------------------*/
        private RSL_clsResult Sys_FirstChange(RQT_clsRequest objRequest, EXC_clsExecution objExecution)
        {
            RSL_clsResult objResult;
            RSL_clsResult objFallback;

            if (Sys_bWritable)
            {   /* Already writable (an earlier change reopened it): nothing   */
                /* to ask or reopen.                                           */
                return(new RSL_clsResult(SYS_OK, "", null));
            }

            /* squashfs can never be written: refused at once, with the   */
            /* read-only notice and no risk question or close.            */
            objResult = Sys_RefuseReadOnlyFilesystem(objRequest);
            if (objResult.RSL_iStatus != SYS_OK)
            {   /* Refused: the notice has been shown; nothing to reopen. */
                return(objResult);
            }

            if (Sys_bRiskWarnings && !Sys_Yes(QNK_enmQuestionKind.Risk, objRequest, objRequest.RQT_lOrigin))
            {   /* The warning was answered No (or not at all): nothing has   */
                /* been closed, so the read-only session simply stays.        */
                return(new RSL_clsResult(SYS_CANCELLED, "The filesystem remains read-only.", null));
            }

            /* The read-only session closed -- its editors stay registered,   */
            /* since the same target is reopened with the same epoch.         */
            objResult = Sys_CloseSession(objRequest, objExecution);
            if (objResult.RSL_iStatus != SYS_OK)
            {   /* The close failed (its final commit, or a stop before it):   */
                /* no reopen, and no change.                                   */
                return(objResult);
            }

            /* Reopened writable.  A failure here is no licence for the   */
            /* change: the caller sees it and stops.                      */
            objResult = Sys_OpenSession(objRequest, objExecution, true, false);
            if (objResult.RSL_iStatus != SYS_OK)
            {   /* Not writable: one read-only reopen is tried, so the user    */
                /* keeps the filesystem open; the writable open's failure is   */
                /* what is returned.                                           */
                objFallback = Sys_OpenSession(objRequest, objExecution, false, false);
                if (objFallback.RSL_iStatus != SYS_OK)
                {   /* Neither open worked: nothing is open.  The editors   */
                    /* stay registered with their content, and the gate     */
                    /* refuses their saves until something is open again.   */
                    /* Both failures are reported.                          */
                    return(new RSL_clsResult(objResult.RSL_iStatus, objResult.RSL_sDetail +
                        " Read-only reopen also failed: " + objFallback.RSL_sDetail, null));
                }
            }

            /* OK (writable), or the writable open's failure after a         */
            /* read-only reopen that worked -- shown in the state, but the   */
            /* change does not go ahead.                                     */
            return(objResult);
        }

        /*--------------------------------------------------------------------*/
        /* Sys_CloseEditors:                                                  */
        /*                                                                    */
        /* Close every editor of one set -- those whose file is in the image, */
        /* or those whose file is a Windows file -- asking each, through its  */
        /* own window, to save, discard or cancel.  One Cancel (or a failed   */
        /* save) stops the whole close and the request it belongs to; editors */
        /* already closed stay closed.  A Save As to Windows whose picker is  */
        /* cancelled is not a Cancel: that editor is asked again.  An editor  */
        /* saved to the other side by Save As moves to the other set, which   */
        /* is why the set is looked at again after every answer.              */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest        : the request that needs them closed (a      */
        /*                         close, an open, Exit ...).                 */
        /*     objExecution      : the running command's services.            */
        /*     bHostDestinations : true for the Windows-file editors, false   */
        /*                         (the default) for the image's.             */
        /*     bViewsReady       : true when the caller has already waited    */
        /*                         for editors still being made (Exit does).  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : OK once the set is empty; otherwise the first  */
        /*                     Cancel, refusal or save failure.               */
        /*--------------------------------------------------------------------*/
        private RSL_clsResult Sys_CloseEditors(RQT_clsRequest objRequest, EXC_clsExecution objExecution,
            bool bHostDestinations = false, bool bViewsReady = false)
        {
            EDR_clsEditor objEditor;
            ANS_clsAnswer objAnswer;
            RSL_clsResult objResult;
            Dictionary<long, EDR_clsEditor>.ValueCollection.Enumerator enmHost;
            bool bAskAgain;

            /* The editors' facts the windows have posted since -- a            */
            /* registration, a Save As completed -- applied first, since they   */
            /* decide which set each editor is in.                              */
            Sys_FlushEditorBindings(objRequest);
            if (!bViewsReady && ((bHostDestinations && Sys_dictHostEditors.Count != 0) ||
                (!bHostDestinations && Sys_lstEditors.Count != 0)))
            {   /* Editors in the set, and nobody has waited for those still   */
                /* being made: one may be registered but have no window yet,   */
                /* and must not be taken for one already closed.  The UI       */
                /* answers EditorsReady once none is being made.               */
                objAnswer = SYS_Ask(QNK_enmQuestionKind.EditorsReady, objRequest, 0);
                if (objAnswer == null || objAnswer.ANS_enmKind != ANK_enmAnswerKind.Closed)
                {   /* No such answer (the command stopped, the UI gone):   */
                    /* which editors exist is not known, so nothing is      */
                    /* closed and the request stops.                        */
                    return(new RSL_clsResult(SYS_CANCELLED, "Viewer creation did not finish.", null));
                }
            }

            /* One editor at a time until the set is empty.  The set is     */
            /* looked at afresh each time, since an answer can change it.   */
            for (;;)
            {   /* One iteration: one editor's question and its answer --   */
                /* closed, saved and asked again, or moved to the other     */
                /* set.  First the facts posted while the last question     */
                /* waited (a registration, a save), in the order posted.    */
                Sys_FlushEditorBindings(objRequest);
                if (bHostDestinations)
                {   /* The Windows-file editors (a table by id). */
                    if (Sys_dictHostEditors.Count == 0)
                    {   /* None left: every one closed or moved to the image's   */
                        /* set.                                                  */
                        break;
                    }

                    /* The first of them, taken straight from the table: only   */
                    /* the worker changes it, so no copy is needed.  The        */
                    /* enumerator is ended before the question below, during    */
                    /* which a completed Save As may move this editor.          */
                    enmHost = Sys_dictHostEditors.Values.GetEnumerator();
                    try
                    {   /* The table is not empty, so there is a first one. */
                        enmHost.MoveNext();
                        objEditor = enmHost.Current;
                    }
                    finally
                    {   /* The enumerator released before anything is asked. */
                        enmHost.Dispose();
                    }
                }
                else
                {   /* The image's editors (a list, in registration order). */
                    if (Sys_lstEditors.Count == 0)
                    {   /* None left -- closed, or moved to Windows by a Save   */
                        /* As.                                                  */
                        break;
                    }

                    /* The oldest first. */
                    objEditor = Sys_lstEditors[0];
                }

                /* A picker open over this editor (its Save As dialog) closed   */
                /* first and waited for: the editor cannot answer its close     */
                /* question while the picker disables it.                       */
                objResult = Sys_CloseInputSet(objRequest, objEditor.EDR_lId, false);
                if (objResult.RSL_iStatus != SYS_OK)
                {   /* The picker would not close: the editor stays, and the   */
                    /* whole request stops.                                    */
                    return(objResult);
                }

                /* Its saves still queued are cancelled first, so the window     */
                /* is not left waiting on a save the worker will never run       */
                /* while it is asked to close.  Then the question, through the   */
                /* editor's window; the last argument tells it the image the     */
                /* editor came from is gone (closed, another epoch or range),    */
                /* so its words can say so.                                      */
                Sys_CancelEditorSaves(objEditor.EDR_lId);
                objAnswer = SYS_Ask(QNK_enmQuestionKind.EditorClose, objRequest, objEditor.EDR_lId,
                    objEditor.EDR_objTarget != null && (Sys_enmMode != MOD_enmMode.Open ||
                    objEditor.EDR_lEpoch != Sys_lEpoch || !objEditor.EDR_objTarget.TGT_SameRange(Sys_objTarget)));
                if (objAnswer != null && objAnswer.ANS_enmKind == ANK_enmAnswerKind.Retry)
                {   /* Retry: a save of the editor's own completed while the   */
                    /* question waited, which may have moved it -- look at     */
                    /* the sets again.                                         */
                    continue;
                }

                if (objAnswer == null || objAnswer.ANS_enmKind == ANK_enmAnswerKind.Cancel || objAnswer.ANS_enmKind == ANK_enmAnswerKind.No)
                {   /* Cancel (or No, or no answer): the editor stays and the    */
                    /* request stops here -- no filesystem is closed.  Editors   */
                    /* closed before this one stay closed.                       */
                    return(new RSL_clsResult(SYS_CANCELLED, "An editor declined to close.", null));
                }

                if (objAnswer.ANS_enmKind == ANK_enmAnswerKind.SelectHostTarget)
                {   /* Save to a Windows file chosen now: the Save As runs as   */
                    /* part of THIS close (Sys_CloseHostTarget), not as a       */
                    /* request queued behind it, which would wait for the       */
                    /* close forever.                                           */
                    if (objAnswer.ANS_objRequest == null)
                    {   /* No Save As request with the answer: nothing may be   */
                        /* picked or written, and the close stops.              */
                        return(new RSL_clsResult(SYS_INTERNAL, "Windows Save As supplied no target intent.", null));
                    }

                    /* Cleared, so an earlier editor's success can never be   */
                    /* taken for this Save As's outcome (the finally below    */
                    /* tests it), nor an earlier Save As's "ask again" for    */
                    /* this one's.                                            */
                    objResult = null;
                    bAskAgain = false;

                    /* The Save As itself.  Choosing to save is not agreeing     */
                    /* to close: its picker and its write must finish, and its   */
                    /* outcome decides whether this close goes on.               */
                    try
                    {   /* Picked, written and recorded, on this sequence. */
                        objResult = Sys_CloseHostTarget(objRequest, objAnswer.ANS_objRequest,
                            objEditor, objExecution, out bAskAgain);
                    }
                    catch (Exception exHostTarget)
                    {   /* It threw: a failure for this Save As, and the close    */
                        /* stops -- neither a close nor a new destination is      */
                        /* assumed from an exception.  The whole exception goes   */
                        /* to the debug log; the detail the editor shows is its   */
                        /* message.                                               */
                        if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                        {   /* Only while the debug log is kept: otherwise the line is never built. */
                            DBGLOG_clsDebugLog.DBGLOG_Write("editor close Save As", exHostTarget.ToString());
                        }

                        /* This Save As's failed result, its detail the exception's message. */
                        objResult = new RSL_clsResult(SYS_INTERNAL, exHostTarget.Message, null);
                    }
                    finally
                    {   /* The Save As request answered, however it ended, and   */
                        /* then released.                                        */
                        if (objResult == null)
                        {   /* Unwound with no outcome (an exception not        */
                            /* caught above): reported as not finished, never   */
                            /* as a success.                                    */
                            objResult = new RSL_clsResult(SYS_INTERNAL,
                                "Windows Save As did not finish.", null);
                        }

                        try
                        {   /* Its result, sent once to the editor that asked;   */
                            /* its picker may already have used the route.       */
                            SYS_Show(new VWM_clsView(VWK_enmViewKind.Result,
                                objAnswer.ANS_objRequest, objResult));
                        }
                        finally
                        {   /* Released even if the result could not be sent,   */
                            /* so the request is never held for good.           */
                            Sys_Release(objAnswer.ANS_objRequest);
                        }
                    }

                    if (objResult.RSL_iStatus != SYS_OK && bAskAgain)
                    {   /* The picker was cancelled with no file chosen: the     */
                        /* editor keeps its content and is ASKED AGAIN -- its    */
                        /* close question shows once more, from which the user   */
                        /* may choose again, or end the close with the           */
                        /* question's own Cancel.  Nothing was written or        */
                        /* recorded, so the same editor is in the same set.      */
                        continue;
                    }

                    if (objResult.RSL_iStatus != SYS_OK)
                    {   /* The Save As refused, or its write failed: the editor   */
                        /* keeps its content and the close stops.                 */
                        return(objResult);
                    }

                    /* Saved: the editor may now be in the other set -- look   */
                    /* again.                                                  */
                    continue;
                }

                if (objAnswer.ANS_enmKind == ANK_enmAnswerKind.Save)
                {   /* Save into the image: run here, on the session still   */
                    /* open, not queued behind this close.                   */
                    if (objAnswer.ANS_objRequest == null)
                    {   /* A Save with no snapshot of the content: nothing to   */
                        /* write, and the close stops.                          */
                        return(new RSL_clsResult(SYS_INTERNAL, "Editor Save supplied no snapshot.", null));
                    }

                    /* This sequence owns the Save request's staged content       */
                    /* until the save returns, and releases it however the save   */
                    /* ends -- a refusal, a failed reopen, an exception.          */
                    try
                    {   /* The ordinary save, with its checks and its answer   */
                        /* to the editor (Sys_SaveEditor).                     */
                        objResult = Sys_SaveEditor(objAnswer.ANS_objRequest, objExecution);
                    }
                    finally
                    {   /* The staged content released; the editor keeps its   */
                        /* own content if the save failed.                     */
                        Sys_Release(objAnswer.ANS_objRequest);
                    }

                    if (objResult.RSL_iStatus != SYS_OK)
                    {   /* The save failed: neither the editor nor the   */
                        /* filesystem is closed.                         */
                        return(objResult);
                    }

                    /* Saved.  The same editor is asked again: it may have been   */
                    /* changed since the snapshot.                                */
                    continue;
                }

                if (objAnswer.ANS_enmKind != ANK_enmAnswerKind.Closed && objAnswer.ANS_enmKind != ANK_enmAnswerKind.Discard)
                {   /* Any answer but Closed or Discard here: not one an      */
                    /* editor close gives, so nothing is assumed closed and   */
                    /* the request stops.                                     */
                    return(new RSL_clsResult(SYS_INTERNAL, "Unexpected editor-close answer.", null));
                }

                /* Closed (or discarded and closed): the window has already    */
                /* let go of its files before answering, so the registration   */
                /* is removed and its read-out file released                   */
                /* (Sys_RemoveEditor), and the new count published.            */
                Sys_RemoveEditor(objEditor, objRequest);
                Sys_PublishState(objRequest);
            }

            /* The set is empty: every editor closed, or moved to the other   */
            /* set.                                                           */
            return(new RSL_clsResult(SYS_OK, "", null));
        }

        /*--------------------------------------------------------------------*/
        /* Sys_CloseHostTarget:                                               */
        /*                                                                    */
        /* An editor's "save to a Windows file" answer to a close question:   */
        /* its picker shown and its write made, both in the window, while     */
        /* this close waits -- then, if the write was made, the editor        */
        /* recorded against its new Windows file.  The completed write's      */
        /* fact is always answered (success or failure), so the editor can    */
        /* settle.  Saving is not agreeing to close: the caller asks again.   */
        /* Nor is a picker cancelled with no file chosen the end of the       */
        /* close: the caller asks the editor's close question again           */
        /* (bAskAgain), and only that question's own Cancel stops the close.  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest   : the close's request, which owns the question.   */
        /*     objIntent    : the editor's Save As request (its target        */
        /*                    input).                                         */
        /*     objEditor    : the editor's registration as it was when asked. */
        /*     objExecution : the running command's services.                 */
        /*     bAskAgain    : OUT -- true when the window says its picker     */
        /*                    was cancelled with no file chosen (the result   */
        /*                    is then CANCELLED and the close question is to  */
        /*                    be asked again); false for every other outcome. */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : OK when written and recorded; otherwise the    */
        /*                     cancel, refusal or failure -- each of which    */
        /*                     stops the close unless bAskAgain is set.       */
        /*--------------------------------------------------------------------*/
        private RSL_clsResult Sys_CloseHostTarget(RQT_clsRequest objRequest,
            RQT_clsRequest objIntent, EDR_clsEditor objEditor, EXC_clsExecution objExecution,
            out bool bAskAgain)
        {
            INA_clsInputArgs objArgs;
            ICV_clsInputView objInput;
            QST_clsQuestion objQuestion;
            ANS_clsAnswer objAnswer;
            RQT_clsRequest objCompleted;
            HBA_clsHostBindingArgs objBinding;
            RSL_clsResult objResult;
            string sDetail;

            /* Not asked again unless the window's answer below says its          */
            /* picker was cancelled: every refusal and failure stops the close.   */
            bAskAgain = false;

            /* The Save As must belong to this editor as it now is: from it,    */
            /* for the entry it showed, at the save count it had.  (Its image   */
            /* may be gone -- a Windows Save As is the way out then -- but it   */
            /* must still be this registration, so an editor changed since      */
            /* cannot pick a file for another.)                                 */
            objArgs = objIntent.RQT_objPayload as INA_clsInputArgs;
            if (objIntent.RQT_enmKind != KND_enmKind.OpenInput || objArgs == null ||
                objArgs.INA_enmKind != INK_enmInputKind.HostSaveTarget ||
                objIntent.RQT_lOrigin != objEditor.EDR_lId ||
                objArgs.INA_sExpectedEntry != objEditor.EDR_sEntry ||
                objArgs.INA_lExpectedOrder != objEditor.EDR_lSaveOrder ||
                Sys_FindEditor(objEditor.EDR_lId) != objEditor)
            {   /* Not a Save As target input, or another editor's, or the   */
                /* editor's entry or save count has moved on: refused        */
                /* before any picker is shown or anything written.           */
                return(new RSL_clsResult(SYS_REFUSED, "The Windows Save As editor binding has changed.", null));
            }

            if (!objExecution.EXC_Checkpoint())
            {   /* A Cancel or an agreed Exit already waiting: it wins, and   */
                /* no picker is shown.                                        */
                return(new RSL_clsResult(SYS_CANCELLED, "Windows Save As cancelled before selection.", null));
            }

            /* The pick and the write, as an EditorClose question whose         */
            /* context is the Save As's input view (the request, the editor,    */
            /* the state): the coordinator knows from that context it is the    */
            /* Windows phase, shows the ordinary picker, makes the write in     */
            /* the window, and answers once both are done.  Nothing is queued   */
            /* behind this worker for it.                                       */
            objInput = new ICV_clsInputView(objIntent, objArgs, objEditor,
                new STV_clsStateView(Sys_enmMode, Sys_objTarget, Sys_lEpoch,
                    Sys_bWritable, true, Sys_bTerminal));
            objQuestion = new QST_clsQuestion(QNK_enmQuestionKind.EditorClose,
                objRequest, objEditor.EDR_lId, null, Sys_objQueue, Sys_evtWake, objInput);
            Sys_objQuestion = objQuestion;
            try
            {   /* Shown, and waited for without letting an Exit or a     */
                /* Cancel take it down (false): a picker and a write in   */
                /* progress must finish first.                            */
                Sys_PresentQuestion(objQuestion);
                objAnswer = Sys_WaitQuestion(objQuestion, false);
            }
            finally
            {   /* The question ended and its event released, however the   */
                /* wait ended -- and no longer the worker's current one.    */
                objQuestion.QST_EndWait();
                Sys_objQuestion = null;
            }

            /* Retry with the completed-save fact means the file was written.    */
            /* Retry with NO fact means the window's picker was cancelled with   */
            /* no file chosen (Main_CloseWindowsInput): nothing written, and     */
            /* the close question is to be asked again.  Anything else -- a      */
            /* write that failed, a picker that could not be shown -- leaves     */
            /* the editor as it was and stops the close, with the window's own   */
            /* reason when it gave one.                                          */
            objCompleted = objAnswer.ANS_objRequest;
            objResult = null;
            try
            {   /* A completed-save fact that came with the answer is     */
                /* answered and released below, even when it is not one   */
                /* this close can use.                                    */
                if (objAnswer.ANS_enmKind == ANK_enmAnswerKind.Retry && objCompleted == null)
                {   /* The picker cancelled: CANCELLED for this Save As, and   */
                    /* the caller asks the editor's close question again --    */
                    /* the question's own Cancel is what ends a close.         */
                    bAskAgain = true;
                    objResult = new RSL_clsResult(SYS_CANCELLED,
                        "No file was chosen; the close question is asked again.", null);
                    return(objResult);
                }

                if (objAnswer.ANS_enmKind != ANK_enmAnswerKind.Retry)
                {   /* Not written: the editor keeps its unsaved content. */
                    sDetail = "Windows Save As did not complete.";
                    if (objAnswer.ANS_sText.Length != 0)
                    {   /* The window's own words for the pick or the write. */
                        sDetail = objAnswer.ANS_sText;
                    }

                    /* CANCELLED: no new file recorded, no close. */
                    objResult = new RSL_clsResult(SYS_CANCELLED, sDetail, null);
                    return(objResult);
                }

                /* Written.  The fact comes with the answer rather than as a     */
                /* second request on the queue.  It must be this editor's, and   */
                /* newer than what is recorded: a save count past the editor's   */
                /* and a revision no older than its saved one -- which names     */
                /* the write that finished, whatever has been typed since.       */
                objBinding = objCompleted.RQT_objPayload as HBA_clsHostBindingArgs;
                if (objCompleted.RQT_enmKind != KND_enmKind.HostEditorSaved ||
                    objCompleted.RQT_lOrigin != objEditor.EDR_lId || objBinding == null ||
                    objBinding.HBA_lSaveOrder <= objEditor.EDR_lSaveOrder ||
                    objCompleted.RQT_lRevision < objEditor.EDR_lSavedRevision ||
                    Sys_FindEditor(objEditor.EDR_lId) != objEditor)
                {   /* Another editor's, or not newer: refused, and the   */
                    /* close stops.                                       */
                    objResult = new RSL_clsResult(SYS_REFUSED,
                        "The Windows save completion does not match this editor.", null);
                    return(objResult);
                }

                /* The editor recorded against its new Windows file             */
                /* (Sys_HostBindingSequence) BEFORE any Cancel or Exit is       */
                /* looked at: the bytes are written whatever came meanwhile,    */
                /* and an Exit that skipped this would then ask the editor as   */
                /* if it still belonged to the image.                           */
                objResult = Sys_HostBindingSequence(objCompleted);
                if (objResult.RSL_iStatus != SYS_OK)
                {   /* The recording failed: said, and the close stops. */
                    return(objResult);
                }
            }
            catch (Exception exHostCompletion)
            {   /* It threw: still a failure answered to the editor (in the   */
                /* finally), so the editor can settle and keep its content.   */
                /* The whole exception to the debug log; the detail the       */
                /* editor shows is its message.                               */
                if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                {   /* Only while the debug log is kept: otherwise the line is never built. */
                    DBGLOG_clsDebugLog.DBGLOG_Write("editor Windows save record", exHostCompletion.ToString());
                }

                /* The failed result the finally answers the editor with. */
                objResult = new RSL_clsResult(SYS_INTERNAL, exHostCompletion.Message, null);
                return(objResult);
            }
            finally
            {   /* The completed-save fact, when one came: answered and     */
                /* released, whatever happened above.  It holds no editor   */
                /* content of its own.                                      */
                if (objCompleted != null)
                {   /* A fact came: it has no queued result of its own, so   */
                    /* this is its only answer.                              */
                    if (objResult == null)
                    {   /* Unwound with no outcome: answered as not finished,   */
                        /* never as a success.                                  */
                        objResult = new RSL_clsResult(SYS_INTERNAL,
                            "The Windows save completion did not finish.", null);
                    }

                    try
                    {   /* Its result to the editor; on success the state     */
                        /* (the editor's new set) is published after it, in   */
                        /* that order.                                        */
                        SYS_Show(new VWM_clsView(VWK_enmViewKind.Result, objCompleted, objResult));
                        if (objResult.RSL_iStatus == SYS_OK)
                        {   /* Recorded: the state after the result. */
                            Sys_PublishState(objRequest);
                        }
                    }
                    finally
                    {   /* Released even if the result could not be sent. */
                        Sys_Release(objCompleted);
                    }
                }
            }

            if (!objExecution.EXC_Checkpoint())
            {   /* A Cancel or an Exit that came while the window worked     */
                /* stops the close now, after the write has been recorded.   */
                return(new RSL_clsResult(SYS_CANCELLED, "Close interrupted after Windows Save As.", null));
            }

            /* OK: written and recorded.  The caller looks at the sets again   */
            /* and asks the editor again if it has unsaved changes.            */
            return(objResult);
        }

        /*--------------------------------------------------------------------*/
        /* Sys_SaveEditor:                                                    */
        /*                                                                    */
        /* An editor's save into the image (Sys_SaveEditorStep), with its     */
        /* answer to the editor guaranteed: an exception becomes a failure,   */
        /* never a silent success.  A save made inside a close (its Save      */
        /* answer) has no queued result of its own, so its EditorSaved view   */
        /* is sent here; a queued Save gets its answer from the scheduler     */
        /* instead, so it is never answered twice.                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest   : the Save request (the editor, its entry and     */
        /*                    revision, the staged content).                  */
        /*     objExecution : the running command's services.                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : the save's outcome; a failure leaves the       */
        /*                     editor's unsaved content in place.             */
        /*--------------------------------------------------------------------*/
        private RSL_clsResult Sys_SaveEditor(RQT_clsRequest objRequest, EXC_clsExecution objExecution)
        {
            RSL_clsResult objResult;

            try
            {   /* The save; its refusals and failures come back as results. */
                objResult = Sys_SaveEditorStep(objRequest, objExecution);
            }
            catch (Exception exSave)
            {   /* It threw: a failure, never taken for a save made.  The    */
                /* whole exception to the debug log; the detail the editor   */
                /* shows is its message.                                     */
                if (DBGLOG_clsDebugLog.DBGLOG_bEnabled)
                {   /* Only while the debug log is kept: otherwise the line is never built. */
                    DBGLOG_clsDebugLog.DBGLOG_Write("editor save", exSave.ToString());
                }

                /* The save's failed result, its detail the exception's message. */
                objResult = new RSL_clsResult(SYS_INTERNAL, exSave.Message, null);
            }

            if (objRequest != objExecution.EXC_ReadRequest())
            {   /* Not the command's own request -- a Save answered inside a   */
                /* close: its only answer is this view, sent here.             */
                SYS_Show(new VWM_clsView(VWK_enmViewKind.EditorSaved, objRequest, objResult));
            }

            /* The outcome, for the caller to go on or stop. */
            return(objResult);
        }

        /*--------------------------------------------------------------------*/
        /* Sys_SaveEditorStep:                                                */
        /*                                                                    */
        /* The save itself: held again to the editor's registration and the   */
        /* open filesystem (a Save answered inside a close never passed the   */
        /* gate), then the first-change reopen if the session is read-only,   */
        /* then the write, then the editor recorded against what it saved.    */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest   : the Save request.                               */
        /*     objExecution : the running command's services.                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : the first refusal or failure, or OK with the   */
        /*                     revision saved.                                */
        /*--------------------------------------------------------------------*/
        private RSL_clsResult Sys_SaveEditorStep(RQT_clsRequest objRequest, EXC_clsExecution objExecution)
        {
            EDR_clsEditor objEditor;
            RSL_clsResult objResult;
            SVA_clsSaveArgs objArgs;

            /* The editor must be registered, and the filesystem the save     */
            /* was made for still the open one -- same epoch, same range --   */
            /* checked here because a Save answered inside a close never      */
            /* went through Sys_Denied.                                       */
            objEditor = Sys_FindEditor(objRequest.RQT_lOrigin);
            objArgs = objRequest.RQT_objPayload as SVA_clsSaveArgs;
            if (objEditor == null || objRequest.RQT_lEpoch != Sys_lEpoch ||
                Sys_enmMode != MOD_enmMode.Open || Sys_objTarget == null || !Sys_objTarget.TGT_SameRange(objRequest.RQT_objTarget))
            {   /* No such editor, or not that open (the same file opened   */
                /* again, or another partition of it): refused.             */
                return(new RSL_clsResult(SYS_REFUSED, "The editor's filesystem is no longer open.", null));
            }

            /* And the save must not be older than what the editor has: the     */
            /* same entry, a revision and a save count no older than those      */
            /* recorded, and the same idea of whether the editor's file is in   */
            /* this filesystem.                                                 */
            if (objArgs == null || objArgs.SVA_sExpectedEntry != objEditor.EDR_sEntry ||
                objRequest.RQT_lRevision < objEditor.EDR_lSavedRevision || objArgs.SVA_lSaveOrder < objEditor.EDR_lSaveOrder ||
                objArgs.SVA_bSameFilesystem != (objEditor.EDR_objTarget != null && objEditor.EDR_lEpoch == Sys_lEpoch &&
                    objEditor.EDR_objTarget.TGT_SameRange(Sys_objTarget)))
            {   /* A queued old snapshot: refused, so it can never write   */
                /* back older content or undo a Save As already made.      */
                return(new RSL_clsResult(SYS_REFUSED, "The editor's entry or saved revision has changed.", null));
            }

            /* A read-only session is reopened writable first, with its   */
            /* question, exactly as for any other change.                 */
            objResult = Sys_FirstChange(objRequest, objExecution);
            if (objResult.RSL_iStatus != SYS_OK)
            {   /* No, or the reopen failed: nothing is written. */
                return(objResult);
            }

            /* The write.  The step looks at the destination again itself and   */
            /* applies the overwrite and type rules there -- what a Save As     */
            /* dialog listed earlier is no licence.                             */
            objResult = Sys_Do(STP_enmStep.Save, objRequest, objExecution);
            if (objResult.RSL_iStatus == SYS_OK)
            {   /* Saved: one new registration record with the entry saved    */
                /* to and the revision written, keeping the editor's read-    */
                /* out file.  Its state is published first (a Save As can     */
                /* move it between sets), then the pane refreshed -- a        */
                /* refresh failure is only information, and never turns the   */
                /* save into a failure or undoes the new entry.               */
                Sys_ReplaceEditor(objEditor, new EDR_clsEditor(objEditor.EDR_lId,
                    Sys_lEpoch, Sys_objTarget, objArgs.SVA_sDestination,
                    objRequest.RQT_lRevision, objEditor.EDR_objResource, objEditor, objArgs.SVA_lSaveOrder));
                Sys_PublishState(objRequest);
                Sys_Refresh(objRequest, objExecution);
            }
            /* The write's outcome.  The read-out file moves with the editor   */
            /* into whichever set it now belongs to.                           */
            return(objResult);
        }

        /*--------------------------------------------------------------------*/
        /* Sys_FindEditor:                                                    */
        /*                                                                    */
        /* An editor's registration by its window id, in either set: the      */
        /* image's (which also holds editors first opened on a Windows file   */
        /* and since saved into the image) or the Windows files'.             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     lId : the editor's window id (kept across its window being     */
        /*           remade, and across a move between sets).                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     EDR_clsEditor : the registration, or null -- a late notice     */
        /*                     about an editor already gone, which callers    */
        /*                     ignore.                                        */
        /*--------------------------------------------------------------------*/
        private EDR_clsEditor Sys_FindEditor(long lId)
        {
            EDR_clsEditor objHost;
            int i;

            /* The image's set first, a list searched in order. */
            for (i = 0; i < Sys_lstEditors.Count; i++)
            {   /* One registration per iteration. */
                if (Sys_lstEditors[i].EDR_lId == lId)
                {   /* Found by id. */
                    return(Sys_lstEditors[i]);
                }
            }

            if (Sys_dictHostEditors.TryGetValue(lId, out objHost))
            {   /* In the Windows files' set (which may still hold a read-   */
                /* out file from the image it was opened from).              */
                return(objHost);
            }

            /* In neither set. */
            return(null);
        }

        /*--------------------------------------------------------------------*/
        /* Sys_RemoveEditor:                                                  */
        /*                                                                    */
        /* An editor gone: its registration removed from both sets and its    */
        /* read-out file released.  The registration goes FIRST, so even if   */
        /* the release fails no save can be made through it again.  A         */
        /* release failure is only warned of; the editor stays gone.          */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objEditor  : the registration -- its window already closed (or */
        /*                  its view never delivered).                        */
        /*     objRequest : the request any warning is addressed to.          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     void : removed; a warning view on a release failure.           */
        /*--------------------------------------------------------------------*/
        private void Sys_RemoveEditor(EDR_clsEditor objEditor, RQT_clsRequest objRequest)
        {
            /* Out of both sets (it is in at most one; removing from the   */
            /* other does nothing).                                        */
            Sys_lstEditors.Remove(objEditor);
            Sys_dictHostEditors.Remove(objEditor.EDR_lId);
            if (objEditor.EDR_objResource != null)
            {   /* A read-out file: the window has let go of it before this   */
                /* is called, so it can be released.                          */
                try
                {   /* Released (OED_Release deletes the file). */
                    objEditor.EDR_objResource.OED_Release();
                }
                catch (Exception exCleanup)
                {   /* It could not be: a warning naming the failure (its    */
                    /* message, not the stack trace) -- the close goes on,   */
                    /* the file stays behind.                                */
                    SYS_Show(new VWM_clsView(VWK_enmViewKind.Warning, objRequest,
                        new RSL_clsResult(SYS_INTERNAL, "Viewer temporary cleanup failed: " + exCleanup.Message, null)));
                }
            }
        }

        /*--------------------------------------------------------------------*/
        /* Sys_PrepareTransition:                                             */
        /*                                                                    */
        /* Everything that must happen before the open filesystem is left for */
        /* something else, in order: the question (Close and Continue, when   */
        /* asked for), the image's inputs closed, the image's editors closed  */
        /* (each asked to save), then the session closed.  Each step stops    */
        /* the rest on a refusal.  With nothing open it still settles editors */
        /* left from a filesystem that could not be reopened.                 */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest   : the request leaving the filesystem (an open,    */
        /*                    Check, New Image, Close, Exit).                 */
        /*     objExecution : the running command's services.                 */
        /*     bAsk         : true to ask Close and Continue first; false for */
        /*                    Close and Exit, which were asked for outright.  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : the session's close outcome, or the first      */
        /*                     refusal or failure before it.                  */
        /*--------------------------------------------------------------------*/
        private RSL_clsResult Sys_PrepareTransition(RQT_clsRequest objRequest, EXC_clsExecution objExecution, bool bAsk)
        {
            RSL_clsResult objResult;

            if (Sys_enmMode != MOD_enmMode.Open)
            {   /* Nothing open -- but a reopen that failed both ways can   */
                /* have left editors registered (Sys_FirstChange).  Their   */
                /* inputs and then they are settled first; there is no      */
                /* session to close.  A Save As to a Windows file is how    */
                /* such an editor keeps its work.                           */
                objResult = Sys_CloseInputSet(objRequest, 0, true);
                if (objResult.RSL_iStatus != SYS_OK)
                {   /* An input would not close: the request stops. */
                    return(objResult);
                }

                if (Sys_lstEditors.Count != 0)
                {   /* Editors left: each asked; a Cancel keeps them and   */
                    /* stops the request.                                  */
                    return(Sys_CloseEditors(objRequest, objExecution));
                }

                /* Nothing open and nothing left over: nothing to do. */
                return(new RSL_clsResult(SYS_OK, "", null));
            }

            if (bAsk && !Sys_Yes(QNK_enmQuestionKind.CloseAndContinue, objRequest, 0))
            {   /* Asked, and not Yes: asked BEFORE any editor's question,   */
                /* so a No closes no editor.                                 */
                return(new RSL_clsResult(SYS_CANCELLED, "The current image remains open.", null));
            }

            /* Every input on the image closed, including pickers the main     */
            /* window owns (not only editors'), after the Yes and before the   */
            /* session goes.                                                   */
            objResult = Sys_CloseInputSet(objRequest, 0, true);
            if (objResult.RSL_iStatus != SYS_OK)
            {   /* An input would not close: the filesystem and the editors   */
                /* stay as they were.                                         */
                return(objResult);
            }

            /* Every editor asked, and any saves it asks for made, before the   */
            /* session closes.                                                  */
            objResult = Sys_CloseEditors(objRequest, objExecution);
            if (objResult.RSL_iStatus != SYS_OK)
            {   /* One Cancel or failed save: the filesystem stays open. */
                return(objResult);
            }

            /* The session closed; BFE_Close's final commit is its outcome. */
            return(Sys_CloseSession(objRequest, objExecution));
        }

        /*--------------------------------------------------------------------*/
        /* Sys_AskNewImageFile:                                               */
        /*                                                                    */
        /* New Image's first step: its dialog -- the file and its size --     */
        /* asked as a question of the NewImage sequence itself, over the      */
        /* main window, so the choice and the file it leads to are one        */
        /* operation and nothing is posted in between for other work to come  */
        /* between.  Browse and Match a Device cannot run inside the dialog   */
        /* (the worker is waiting on its answer, so a read asked from it      */
        /* would wait behind it): each ends the dialog with what was typed,   */
        /* and this asks the file or the disk itself and shows the dialog     */
        /* again with the fields put back, until it is accepted or            */
        /* cancelled.  On acceptance the request becomes the NewImage it      */
        /* describes, in place, before anything is closed or made.            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest   : the NewImage request, its payload the           */
        /*                    NIQ_clsNewImageQuestion naming the dialog's     */
        /*                    start folder; its target, flags and payload     */
        /*                    are replaced on acceptance.                     */
        /*     objExecution : the running command's services (the questions,  */
        /*                    the disk list's step).                          */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : SYS_OK once the file and size are accepted,    */
        /*                     the request changed; SYS_CANCELLED when the    */
        /*                     dialog was cancelled or interrupted.           */
        /*--------------------------------------------------------------------*/
        private RSL_clsResult Sys_AskNewImageFile(RQT_clsRequest objRequest, EXC_clsExecution objExecution)
        {
            NIQ_clsNewImageQuestion objAsk;
            NIC_clsNewImageChoice objChoice;
            ANS_clsAnswer objAnswer;
            RSL_clsResult objListResult;
            EDV_clsDeviceResult objListed;
            DEV_clsDisk objDisk;
            object objDiskContext;
            string sPath;
            decimal decSize;
            int iUnit;

            /* The first showing's context is the request's own payload:   */
            /* the start folder, nothing typed yet.  Every later showing   */
            /* gets a new record carrying the fields to put back.          */
            objAsk = (NIQ_clsNewImageQuestion)objRequest.RQT_objPayload;

            while (true)
            {   /* One showing of the dialog per iteration, and whatever its   */
                /* Browse or Match asks for after it.                          */

                /* The dialog, over the main window (window 0).  Only a Yes    */
                /* carrying its record counts: Cancel, the Cancel a question   */
                /* is answered when the command is stopped while it waits      */
                /* (EXC_Ask), or a Yes without the record all end New Image    */
                /* the same way.                                               */
                objAnswer = objExecution.EXC_Ask(QNK_enmQuestionKind.NewImageFile, 0, objAsk);
                objChoice = null;
                if (objAnswer != null && objAnswer.ANS_enmKind == ANK_enmAnswerKind.Yes)
                {   /* Accepted, or Browse or Match pressed: the record says   */
                    /* which (a guard tests its type below).                   */
                    objChoice = objAnswer.ANS_objValue as NIC_clsNewImageChoice;
                }

                if (objChoice == null)
                {   /* Cancelled: nothing has been closed or made, so there   */
                    /* is nothing to undo.                                    */
                    return(new RSL_clsResult(SYS_CANCELLED, "New image cancelled.", null));
                }

                if (!objChoice.NIC_bBrowse && !objChoice.NIC_bMatch)
                {   /* OK, every check of the dialog passed (and its replace   */
                    /* question answered there): the request becomes the       */
                    /* NewImage it describes.  The request is this worker's    */
                    /* while it runs, and the steps after this (the close,     */
                    /* the create, the partition window) and the window's      */
                    /* result handler (MainCommand.cs) all read it -- so it    */
                    /* is changed in place, before any of them.                */

                    /* The target: the whole new file (offset 0, length 0 for   */
                    /* "to the end"), no device text, the chosen size as the    */
                    /* disk's size -- which the create step sets the file to    */
                    /* -- and 512-byte sectors, the size a file is given.       */
                    objRequest.RQT_objTarget = new TGT_clsTarget(objChoice.NIC_sPath, 0, 0, "", objChoice.NIC_ullBytes, 512);

                    /* The replace choice as the option's bit 0 (value 1),      */
                    /* the only thing that lets the create step open with       */
                    /* Create, which empties an existing file, instead of       */
                    /* CreateNew, which fails on one (CommandNativeSteps.cs).   */
                    objRequest.RQT_uiFlags = 0;
                    if (objChoice.NIC_bOverwrite)
                    {   /* The file exists and the dialog's question was   */
                        /* answered replace.                               */
                        objRequest.RQT_uiFlags = 1;
                    }

                    /* The payload cleared: the question is answered, and a   */
                    /* null payload is what tells the result handler the      */
                    /* target is the chosen file.                             */
                    objRequest.RQT_objPayload = null;

                    /* Accepted: the sequence goes on to the close and the create. */
                    return(new RSL_clsResult(SYS_OK, "", null));
                }

                /* Browse or Match: the fields as they were typed, kept to be   */
                /* put back when the dialog is shown again -- each picker       */
                /* below replaces only its own field, and only when it is       */
                /* answered.                                                    */
                sPath = objChoice.NIC_sPath;
                decSize = objChoice.NIC_decSize;
                iUnit = objChoice.NIC_iUnit;

                if (objChoice.NIC_bBrowse)
                {   /* Browse: the standard save dialog, starting where the   */
                    /* typed path points.  A file named there replaces the    */
                    /* path; a Cancel leaves it as typed.                     */
                    objAnswer = objExecution.EXC_Ask(QNK_enmQuestionKind.NewImagePath, 0, sPath);
                    if (objAnswer != null && objAnswer.ANS_enmKind == ANK_enmAnswerKind.Yes &&
                        !String.IsNullOrEmpty(objAnswer.ANS_sText))
                    {   /* A file was named: its full path. */
                        sPath = objAnswer.ANS_sText;
                    }
                }
                else
                {   /* Match a Device: the disks listed now, on this worker,   */
                    /* and the disk picker asked with them.  A list that       */
                    /* could not be read is asked too, as its reason, so the   */
                    /* picker's question says why and the dialog comes back    */
                    /* with the size unchanged -- never an empty list, which   */
                    /* would read as "no disks attached".                      */
                    objListResult = Sys_Do(STP_enmStep.ListDevices, objRequest, objExecution);
                    objListed = objListResult.RSL_objPayload as EDV_clsDeviceResult;
                    if (objListResult.RSL_iStatus == SYS_CANCELLED)
                    {   /* The command was stopped (a Cancel, or an Exit        */
                        /* agreed) before the disks were read: New Image ends   */
                        /* here, as a Cancel of its dialog would end it.        */
                        return(new RSL_clsResult(SYS_CANCELLED, "New image cancelled.", null));
                    }

                    if (objListResult.RSL_iStatus != SYS_OK)
                    {   /* The read failed or was refused: its own reason. */
                        objDiskContext = objListResult.RSL_sDetail;
                    }
                    else if (objListed == null || objListed.EDV_lstDisks == null)
                    {   /* A success with no list: a reason of its own, as the   */
                        /* device pickers give it.                               */
                        objDiskContext = "Device enumeration returned no usable facts.";
                    }
                    else
                    {   /* The disks, possibly none (the picker says so). */
                        objDiskContext = objListed.EDV_lstDisks;
                    }

                    /* The picker.  A disk chosen there puts its exact length   */
                    /* into the size, in bytes (unit 0) -- the dialog then      */
                    /* rounds it down to whole sectors, as every size is.  A    */
                    /* disk larger than the size box can hold is refused by     */
                    /* the picker's question itself, answered Cancel, so the    */
                    /* size stays as typed.                                     */
                    objAnswer = objExecution.EXC_Ask(QNK_enmQuestionKind.NewImageDisk, 0, objDiskContext);
                    objDisk = null;
                    if (objAnswer != null && objAnswer.ANS_enmKind == ANK_enmAnswerKind.Yes)
                    {   /* A disk was chosen (a guard tests its type below). */
                        objDisk = objAnswer.ANS_objValue as DEV_clsDisk;
                    }

                    if (objDisk != null)
                    {   /* Its length, in bytes. */
                        decSize = (decimal)objDisk.DEV_stInfo.DVI_ullSize;
                        iUnit = 0;
                    }
                }

                /* The next showing's context: the same start folder, with     */
                /* the fields put back, the picker's answer already in them.   */
                objAsk = new NIQ_clsNewImageQuestion(objAsk.NIQ_sStartFolder, true, sPath, decSize, iUnit);
            }
        }

        /*--------------------------------------------------------------------*/
        /* Sys_Sequence:                                                      */
        /*                                                                    */
        /* One request, run whole: the gate first (Sys_Denied), then the      */
        /* sequence for its kind, each step only after the one before it      */
        /* succeeded.  The scheduler calls this for every request it takes,   */
        /* and turns the outcome into the request's result.                   */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest   : the request taken from the queue.               */
        /*     objExecution : the running command's services (questions,      */
        /*                    checkpoints, views), valid until this returns.  */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : the request's outcome -- REFUSED by the gate,  */
        /*                     or the sequence's own.                         */
        /*--------------------------------------------------------------------*/
        private RSL_clsResult Sys_Sequence(RQT_clsRequest objRequest, EXC_clsExecution objExecution)
        {
            RSL_clsResult objResult;
            string sDenied;
            TGT_clsTarget objSavedTarget;
            bool bReopen;
            EDR_clsEditor objEditor;

            /* The gate: the state now decides, not what the window showed   */
            /* or the state when the request was posted.                     */
            sDenied = Sys_Denied(objRequest);
            if (sDenied.Length != 0)
            {   /* Refused: no engine call is made; the scheduler releases   */
                /* what the request owns along with its result.              */
                return(new RSL_clsResult(SYS_REFUSED, sDenied, null));
            }

            switch (objRequest.RQT_enmKind)
            {   /* One case per kind: its whole sequence. */
                case KND_enmKind.Cancel:
                    /* Cancel: the stop was already raised when it was   */
                    /* posted; this only answers the request.            */
                    return(new RSL_clsResult(SYS_OK, "Cancellation delivered.", null));
                case KND_enmKind.Exit:
                    /* Exit (or the restart as Administrator): its own   */
                    /* sequence.                                         */
                    return(Sys_ExitSequence(objRequest, objExecution));
                case KND_enmKind.OpenImageFile:
                    /* Open or Check an image file: the file looked at and    */
                    /* its partition chosen before anything open is closed.   */
                    return(Sys_ImageFileSequence(objRequest, objExecution));
                case KND_enmKind.OpenDevice:
                    /* Open or Check a device: likewise, in one sequence. */
                    return(Sys_DeviceOpenSequence(objRequest, objExecution));
                case KND_enmKind.Open:
                    /* Open a filesystem at a given range: the old one left   */
                    /* first (asked, editors closed, session closed) -- any   */
                    /* refusal there stops before anything changes.           */
                    objResult = Sys_PrepareTransition(objRequest, objExecution, true);
                    if (objResult.RSL_iStatus != SYS_OK)
                    {   /* The old filesystem stays; its refusal or failure is   */
                        /* the result, and nothing new is opened.                */
                        return(objResult);
                    }

                    /* Only now the new range opened, read-only, a new epoch. */
                    objResult = Sys_OpenSession(objRequest, objExecution, false, true);
                    if (objResult.RSL_iStatus != SYS_OK)
                    {   /* Not opened: no listing. */
                        return(objResult);
                    }

                    /* Opened: the root listed, and that listing is the   */
                    /* result.                                            */
                    return(Sys_Refresh(objRequest, objExecution));
                case KND_enmKind.Close:
                    /* Close Image: the editors asked and closed, the session   */
                    /* closed -- no Close and Continue question, the user       */
                    /* asked for this.                                          */
                    return(Sys_PrepareTransition(objRequest, objExecution, false));
                case KND_enmKind.MakeWritable:
                    /* Enable Write: the first-change reopen, asked for   */
                    /* outright.                                          */
                    objResult = Sys_FirstChange(objRequest, objExecution);
                    if (objResult.RSL_iStatus == SYS_OK)
                    {   /* Writable: the same folder listed again (the epoch   */
                        /* and the editors are kept).                          */
                        Sys_Refresh(objRequest, objExecution);
                    }

                    /* The reopen's outcome; the listing's is only   */
                    /* information.                                  */
                    return(objResult);
                case KND_enmKind.MakeReadOnly:
                    /* Back to read-only: the same target reopened, the   */
                    /* editors and their read-out files kept.             */
                    if (!Sys_bWritable)
                    {   /* Already read-only: no close, no reopen, the   */
                        /* editors untouched.                            */
                        return(new RSL_clsResult(SYS_OK, "", null));
                    }

                    /* The writable session closed first. */
                    objResult = Sys_CloseSession(objRequest, objExecution);
                    if (objResult.RSL_iStatus != SYS_OK)
                    {   /* The close (its final commit) failed: no reopen. */
                        return(objResult);
                    }

                    /* Reopened read-only, the epoch kept: the range has not   */
                    /* changed, so the editors' work stays valid.              */
                    objResult = Sys_OpenSession(objRequest, objExecution, false, false);
                    if (objResult.RSL_iStatus == SYS_OK)
                    {   /* Reopened: the same folder listed again. */
                        Sys_Refresh(objRequest, objExecution);
                    }

                    /* The reopen's outcome; the listing's is only   */
                    /* information.                                  */
                    return(objResult);
                case KND_enmKind.SaveEditor:
                    /* An editor's Save: written and answered (the scheduler     */
                    /* sends its answer, as this is the command's own request)   */
                    /* -- the window starts nothing after it.                    */
                    return(Sys_SaveEditor(objRequest, objExecution));
                case KND_enmKind.OpenInput:
                    /* An input (a picker or a value dialog): registered and   */
                    /* sent to the window, after its own checks.               */
                    return(Sys_OpenInputSequence(objRequest, objExecution));
                case KND_enmKind.ViewerFallback:
                    /* The text-editor fallback after the structured editor    */
                    /* refused a file: asked, then opened as a fresh request   */
                    /* of its own.                                             */
                    return(Sys_ViewerFallbackSequence(objRequest, objExecution));
                case KND_enmKind.EditorReady:
                    /* An editor's window has finished being made. */
                    objEditor = Sys_FindEditor(objRequest.RQT_lOrigin);
                    if (objEditor != null)
                    {   /* Still registered: the record replaced by one         */
                        /* marked ready (the last argument), all else kept.     */
                        /* A later close can then tell a real window from one   */
                        /* registered but not yet made.                         */
                        Sys_ReplaceEditor(objEditor, new EDR_clsEditor(objEditor.EDR_lId,
                            objEditor.EDR_lEpoch, objEditor.EDR_objTarget, objEditor.EDR_sEntry,
                            objEditor.EDR_lSavedRevision, objEditor.EDR_objResource, objEditor,
                            objEditor.EDR_lSaveOrder, true));
                    }

                    /* Done; a notice for an editor already gone is ignored. */
                    return(new RSL_clsResult(SYS_OK, "", null));
                case KND_enmKind.InputClosed:
                    /* An input's window has gone: its registration removed;   */
                    /* nothing follows.                                        */
                    Sys_RemoveInput(objRequest.RQT_lOrigin);

                    /* Done: an input holds no content or session. */
                    return(new RSL_clsResult(SYS_OK, "", null));
                case KND_enmKind.RegisterHostEditor:
                case KND_enmKind.HostEditorSaved:
                    /* A Windows-file editor registered, or its save made:   */
                    /* facts recorded (Sys_HostBindingSequence) -- never a   */
                    /* licence to write a Windows file.                      */
                    return(Sys_HostBindingSequence(objRequest));
                case KND_enmKind.EditorClosed:
                    /* An editor's window has closed: its registration and   */
                    /* read-out file released.                               */
                    objEditor = Sys_FindEditor(objRequest.RQT_lOrigin);
                    if (objEditor != null)
                    {   /* Still registered (a second notice finds nothing). */
                        Sys_RemoveEditor(objEditor, objRequest);
                    }

                    /* Done; a notice for an editor already gone is ignored. */
                    return(new RSL_clsResult(SYS_OK, "", null));
                case KND_enmKind.Check:
                    /* Check Filesystem.  The target kept now, before the   */
                    /* session it may belong to is closed: the open         */
                    /* filesystem's own range when it is the one checked    */
                    /* (and then it is reopened after the check window      */
                    /* closes), else the request's.                         */
                    objSavedTarget = objRequest.RQT_objTarget;
                    bReopen = Sys_enmMode == MOD_enmMode.Open;
                    if (bReopen)
                    {   /* Checking the open filesystem: its own range, as   */
                        /* the session holds it.                             */
                        objSavedTarget = Sys_objTarget;
                    }

                    /* The risk warning first, before any editor is asked or   */
                    /* anything closed: the check window can repair.           */
                    if (Sys_bRiskWarnings && !Sys_Yes(QNK_enmQuestionKind.Risk, objRequest, objRequest.RQT_lOrigin))
                    {   /* Not Yes: nothing has been closed. */
                        return(new RSL_clsResult(SYS_CANCELLED, "Check window declined.", null));
                    }

                    /* The open filesystem left (asked, editors, session), so   */
                    /* no session holds it while the check runs.                */
                    objResult = Sys_PrepareTransition(objRequest, objExecution, true);
                    if (objResult.RSL_iStatus != SYS_OK)
                    {   /* Refused or failed: no check mode, no window. */
                        return(objResult);
                    }

                    /* Check mode recorded BEFORE the window's view is sent:   */
                    /* the window can post its Repair or its close at once,    */
                    /* and those are held to this mode and window id.          */
                    Sys_enmMode = MOD_enmMode.Check;
                    Sys_objTarget = objSavedTarget;
                    Sys_lModeWindow = objRequest.RQT_lSerial;
                    Sys_bReopenAfterCheck = bReopen;
                    Sys_PublishState(objRequest);
                    if (!SYS_Show(new VWM_clsView(VWK_enmViewKind.CheckOpen, objRequest, objSavedTarget)))
                    {   /* The view could not be sent: check mode is left     */
                        /* again and what was open put back (by running the   */
                        /* CheckClosed sequence, Sys_CheckViewUnavailable),   */
                        /* so no check mode stays with no window to end it.   */
                        return(Sys_CheckViewUnavailable(objRequest, objExecution));
                    }

                    /* The first, report-only check run in the window.  It is    */
                    /* run with a copy of the request aimed at the kept target   */
                    /* (Sys_RequestForTarget), so its run views reach the        */
                    /* window even when this request was a file or device        */
                    /* pick with an answer of its own.                           */
                    return(Sys_RunCheckView(Sys_RequestForTarget(objRequest, objSavedTarget), objExecution, false));
                case KND_enmKind.Repair:
                    /* The check window's Repair: asked over that window,   */
                    /* No the default.                                      */
                    if (!Sys_Yes(QNK_enmQuestionKind.Repair, objRequest, Sys_lModeWindow))
                    {   /* Not Yes: nothing written. */
                        return(new RSL_clsResult(SYS_CANCELLED, "Repair declined.", null));
                    }

                    /* Asked BEFORE the run begins, so a No leaves the window   */
                    /* with its last report and its buttons as they were.       */
                    return(Sys_RunCheckView(objRequest, objExecution, true));
                case KND_enmKind.CheckClosed:
                    /* The check window has closed: the mode ends, and the    */
                    /* filesystem it checked is reopened if it was the open   */
                    /* one.                                                   */
                    if (Sys_enmMode != MOD_enmMode.Check || objRequest.RQT_lOrigin != Sys_lModeWindow)
                    {   /* Not the check window that owns the mode (an older   */
                        /* one, or no check open): ignored -- it may neither   */
                        /* end another's mode nor reopen its target.           */
                        return(new RSL_clsResult(SYS_OK, "", null));
                    }

                    /* The target and the reopen kept, then the mode ended. */
                    objSavedTarget = Sys_objTarget;
                    bReopen = Sys_bReopenAfterCheck;
                    Sys_enmMode = MOD_enmMode.None;
                    Sys_objTarget = null;
                    Sys_bReopenAfterCheck = false;
                    if (bReopen)
                    {   /* It was the open filesystem: reopened read-only as a   */
                        /* new epoch (no editors remain -- they were closed      */
                        /* for the check), and the root listed.                  */
                        objResult = Sys_OpenSession(Sys_RequestForTarget(objRequest, objSavedTarget), objExecution, false, true);
                        if (objResult.RSL_iStatus == SYS_OK)
                        {   /* Reopened: listed. */
                            Sys_Refresh(objRequest, objExecution);
                        }

                        /* The reopen's outcome; the listing's is only   */
                        /* information.                                  */
                        return(objResult);
                    }

                    /* Mode ended; nothing to reopen. */
                    return(new RSL_clsResult(SYS_OK, "", null));
                case KND_enmKind.NewImage:
                    /* New Image.  NO risk warning here: the partition window     */
                    /* opens over the empty file this sequence makes, which       */
                    /* holds nothing to lose (the warning guards the window       */
                    /* over an existing file or device, in                        */
                    /* Sys_OpenPartitionOperation).  Replacing an existing file   */
                    /* is confirmed inside New Image's own dialog, which this     */
                    /* sequence asks first.                                       */

                    if (objRequest.RQT_objPayload is NIQ_clsNewImageQuestion)
                    {   /* File > New Image File's request, carrying only where   */
                        /* the dialog starts: the file and its size asked now,    */
                        /* as questions of this one sequence, and the request     */
                        /* turned into the NewImage they describe.  A request     */
                        /* that names its file already has no payload and goes    */
                        /* straight on (a guard: nothing posts one).              */
                        objResult = Sys_AskNewImageFile(objRequest, objExecution);
                        if (objResult.RSL_iStatus != SYS_OK)
                        {   /* Cancelled, or an answer without its record:        */
                            /* nothing asked about the open filesystem, nothing   */
                            /* closed and no file made.                           */
                            return(objResult);
                        }
                    }

                    /* The open filesystem left first (asked, editors,      */
                    /* session); a refusal stops before any file is made.   */
                    objResult = Sys_PrepareTransition(objRequest, objExecution, true);
                    if (objResult.RSL_iStatus != SYS_OK)
                    {   /* Refused or failed: no file made. */
                        return(objResult);
                    }

                    /* The new image file made. */
                    objResult = Sys_Do(STP_enmStep.CreateImage, objRequest, objExecution);
                    if (objResult.RSL_iStatus != SYS_OK)
                    {   /* Not made: no partition window. */
                        return(objResult);
                    }

                    /* Made: the partition window opened over it, without the   */
                    /* risk warning, as said above.                             */
                    objResult = Sys_OpenPartitionSequence(objRequest, objExecution);
                    if (objResult.RSL_iStatus != SYS_OK)
                    {   /* The window could not open -- but the file exists:   */
                        /* the message says it was made, so the failure is     */
                        /* not read as the creation's.                         */
                        return(new RSL_clsResult(objResult.RSL_iStatus,
                            "The image file was created, but its partition window could not be opened: " + objResult.RSL_sDetail,
                            objResult.RSL_objPayload));
                    }

                    /* Made and its window open: the layout's result. */
                    return(objResult);
                case KND_enmKind.OpenPartition:
                    /* The partition window over a file or device, as ONE      */
                    /* operation: the disk asked, the warning naming it, the   */
                    /* restart as Administrator when a device needs one, or    */
                    /* the open filesystem closed and the window opened        */
                    /* (Sys_OpenPartitionOperation).                           */
                    return(Sys_OpenPartitionOperation(objRequest, objExecution));
                case KND_enmKind.ClosePartition:
                case KND_enmKind.OpenPartitionEntry:
                    /* The partition window's Close, or its Open of one    */
                    /* partition: the window closed, then (for Open) the   */
                    /* partition opened.                                   */
                    return(Sys_PartitionEndSequence(objRequest, objExecution));
                case KND_enmKind.PartitionClosed:
                    /* The partition window has closed by itself. */
                    if (Sys_enmMode == MOD_enmMode.Partition && objRequest.RQT_lOrigin == Sys_lModeWindow)
                    {   /* It is the window that owns the mode: the mode      */
                        /* ends.  An older window's notice changes nothing.   */
                        Sys_enmMode = MOD_enmMode.None;
                        Sys_objTarget = null;
                    }

                    /* Done; a notice from a window already gone is ignored. */
                    return(new RSL_clsResult(SYS_OK, "", null));
                case KND_enmKind.DragEnded:
                    /* A drag out of the image has ended: its hold on the   */
                    /* queue released, so ordinary work can run again.      */
                    lock (Sys_objQueue)
                    {   /* Under the queue lock, where the hold is read. */
                        if (Sys_lDrag == objRequest.RQT_lRevision)
                        {   /* This drag's own hold (by its number): released.   */
                            /* An older drag's late notice releases nothing.     */
                            Sys_lDrag = 0;
                        }
                    }

                    /* Done. */
                    return(new RSL_clsResult(SYS_OK, "", null));
                case KND_enmKind.SetRiskWarnings:
                    /* The risk warnings option.  Turning them OFF (flags 0)   */
                    /* while they are on needs two Yes answers.                */
                    if (objRequest.RQT_uiFlags == 0 && Sys_bRiskWarnings)
                    {   /* Off: both questions, in turn; an Exit may take   */
                        /* either down meanwhile.                           */
                        if (!Sys_Yes(QNK_enmQuestionKind.DisableRiskWarnings, objRequest, 0) ||
                            !Sys_Yes(QNK_enmQuestionKind.ConfirmDisableRiskWarnings, objRequest, 0))
                        {   /* Either not Yes: the warnings stay on, and the   */
                            /* result is not OK, so the window neither saves   */
                            /* the setting nor moves its tick.                 */
                            return(new RSL_clsResult(SYS_CANCELLED, "The risk warnings remain on.", null));
                        }
                    }

                    /* The setting taken.  Turning them on asks nothing; nor   */
                    /* does turning them off when they are already off.        */
                    Sys_bRiskWarnings = objRequest.RQT_uiFlags != 0;

                    /* OK: only now does the window save the setting and tick   */
                    /* its menu.                                                */
                    return(new RSL_clsResult(SYS_OK, "", null));
                default:
                    /* Every other kind -- the listings, edits, copies and   */
                    /* raw copies -- runs in the operations' sequence        */
                    /* (CommandOperations.cs), on this worker as well.       */
                    return(Sys_OperationSequence(objRequest, objExecution));
            }
        }

        /*------------------------------------------------------------------*/
        /* Sys_RequestForTarget:                                            */
        /*                                                                  */
        /* A copy of a request aimed at another target: the same kind,      */
        /* origin, lifetime, revision, flags, payload, names and paths,     */
        /* with only the target replaced.  A sequence uses it where a step  */
        /* must run against coordinates it kept itself rather than the      */
        /* range the request carries -- a check's target retained before    */
        /* its session closed, and the partition window's whole disk for    */
        /* the layout read after a change.  The copy is never queued and    */
        /* owns nothing: it borrows the payload and the path list from the  */
        /* request, whose lifetime spans the whole sequence.                */
        /*                                                                  */
        /* Arguments:                                                       */
        /*     objRequest : the request copied: its routing and arguments.  */
        /*     objTarget  : the target the copy carries instead.            */
        /*                                                                  */
        /* Returns:                                                         */
        /*     RQT_clsRequest : the unqueued copy.                          */
        /*------------------------------------------------------------------*/
        private static RQT_clsRequest Sys_RequestForTarget(RQT_clsRequest objRequest, TGT_clsTarget objTarget)
        {
            RQT_clsRequest objBound;

            /* The copy: every field of the request but the target, which      */
            /* is the one given -- the path list shared rather than copied.    */
            /* It is never queued and releases nothing: the payload and the    */
            /* paths are the request's own, borrowed for the sequence, which   */
            /* the request outlives.                                           */
            objBound = new RQT_clsRequest(objRequest.RQT_enmKind, objRequest.RQT_lOrigin,
                objRequest.RQT_lEpoch, objTarget, null, objRequest.RQT_lRevision,
                objRequest.RQT_uiFlags, objRequest.RQT_objPayload,
                objRequest.RQT_sIdentity, objRequest.RQT_sStream, objRequest.RQT_sFolder);
            objBound.RQT_lstPaths = objRequest.RQT_lstPaths;

            /* The copy, for the caller's step. */
            return(objBound);
        }

        /*--------------------------------------------------------------------*/
        /* Sys_OpenPartitionOperation:                                        */
        /*                                                                    */
        /* File > Partition/Format, Image File or Device, as ONE operation    */
        /* from the press to the window, ordered so that every question that  */
        /* can be answered No comes before anything is closed, saved or       */
        /* restarted: the disk or disk image first, then the risk warning     */
        /* naming it.  Only then, for a device in a copy without              */
        /* Administrator rights, the restart as Administrator -- which closes */
        /* everything as Exit does and hands the chosen disk to the elevated  */
        /* copy, which asks neither again -- or otherwise the open filesystem */
        /* closed, the layout read and the window opened.                     */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest   : the OpenPartition request; changed in place as  */
        /*                    the disk is found, and made the restart's Exit  */
        /*                    when one is needed.                             */
        /*     objExecution : the running command's services.                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : the layout read's outcome when the window      */
        /*                     opened; the restart's Exit outcome (OK ends    */
        /*                     this copy, as an agreed Exit does); otherwise  */
        /*                     CANCELLED or REFUSED with the reason.          */
        /*--------------------------------------------------------------------*/
        private RSL_clsResult Sys_OpenPartitionOperation(RQT_clsRequest objRequest, EXC_clsExecution objExecution)
        {
            RSL_clsResult objResult;
            POA_clsPartitionOpenArgs objOpen;
            bool bWarned;

            /* Whether the warning has been answered already: a disk handed    */
            /* over by the copy that restarted this one as Administrator,      */
            /* which asked the warning before the restart.  Read before the    */
            /* disk is found, since finding it replaces the payload.  A disk   */
            /* reaches POA_sDisk from the command line only on that restart:   */
            /* Main drops a --disk given without --after (Program.cs).         */
            objOpen = objRequest.RQT_objPayload as POA_clsPartitionOpenArgs;
            bWarned = objOpen != null && objOpen.POA_bAskDevice && objOpen.POA_sDisk.Length != 0;

            /* The disk or disk image found first -- asked, or a restart's   */
            /* taken from the list -- before anything open is asked about,   */
            /* so a cancelled choice closes nothing (as Open's and New       */
            /* Image's sequences do).                                        */
            objResult = Sys_AskPartitionTarget(objRequest, objExecution);
            if (objResult.RSL_iStatus != SYS_OK)
            {   /* Not chosen, unreadable, or a restart's disk gone: nothing   */
                /* asked about the open filesystem, nothing closed, no         */
                /* window.                                                     */
                return(objResult);
            }

            /* Then the warning (what the window can destroy), naming the       */
            /* disk now chosen: before the open filesystem is asked about and   */
            /* before any restart, so a No leaves everything as it was.  It     */
            /* is not asked again in the copy a restart started for this        */
            /* disk -- that copy's request says so (bWarned).                   */
            if (!bWarned && Sys_bRiskWarnings && !Sys_Yes(QNK_enmQuestionKind.Risk, objRequest, objRequest.RQT_lOrigin))
            {   /* Not Yes: nothing closed, saved or restarted, no window. */
                return(new RSL_clsResult(SYS_CANCELLED, "Partition window declined.", null));
            }

            if (objRequest.RQT_objTarget.TGT_sPath.StartsWith(DEV_clsDisk.DEV_PHYSICAL_PREFIX, StringComparison.OrdinalIgnoreCase) &&
                !APP_clsProgram.APP_IsProcessElevated())
            {   /* A device, in a copy without Administrator rights, which     */
                /* reading or writing a disk needs.  The request becomes the   */
                /* restart's Exit IN PLACE -- payload and kind -- carrying     */
                /* the chosen disk, and runs the Exit's own sequence now.      */
                /* In place, not posted as a second request: the scheduler     */
                /* and the question waits read the RUNNING request, so as an   */
                /* Exit its own questions are not cancelled once the program   */
                /* turns terminal, and nothing can run in between.  The        */
                /* restart's question is the consent to closing what is        */
                /* open -- its editors are asked as Exit asks them -- so       */
                /* "Close the open filesystem and continue?" is not asked as   */
                /* well.                                                       */
                objRequest.RQT_objPayload = new ELA_clsElevationArgs("Partitioning a device", "partition-device",
                    objRequest.RQT_objTarget.TGT_sPath);
                objRequest.RQT_enmKind = KND_enmKind.Exit;

                /* The Exit's outcome: OK once the elevated copy has started   */
                /* (this copy then stops); otherwise why it did not, with      */
                /* this copy running on.                                       */
                return(Sys_ExitSequence(objRequest, objExecution));
            }

            /* The open filesystem left (Close and Continue asked, its inputs   */
            /* closed, each editor asked to save, discard or cancel as Exit     */
            /* asks it, the session closed) -- the window needs the disk to     */
            /* itself.  With nothing open it only settles editors left from a   */
            /* closed filesystem.                                               */
            objResult = Sys_PrepareTransition(objRequest, objExecution, true);
            if (objResult.RSL_iStatus != SYS_OK)
            {   /* Declined, an editor's Cancel or a failed save, or the   */
                /* close failed: what was open stays, no window.           */
                return(objResult);
            }

            /* Then the layout read and the window. */
            return(Sys_OpenPartitionSequence(objRequest, objExecution));
        }

        /*--------------------------------------------------------------------*/
        /* Sys_AskPartitionTarget:                                            */
        /*                                                                    */
        /* File > Partition/Format's first step: the disk or disk image the   */
        /* window is to show, asked as a question of the OpenPartition        */
        /* request itself -- a device with the device picker, an image file   */
        /* with Windows' open dialog -- or, for the copy a restart as         */
        /* Administrator started, the disk the restarting copy chose, found   */
        /* in the list with no question; and the request made, in place, the  */
        /* window over it (its target the whole disk or file, its caption).   */
        /* It runs before anything open is asked about or closed, so a        */
        /* cancelled choice changes nothing, and nothing is posted in         */
        /* between for other work to come between.                            */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest   : the OpenPartition request; its payload says     */
        /*                    what to ask (POA_bAskDevice -- with POA_sDisk,  */
        /*                    the disk to find instead -- or a                */
        /*                    PIQ_clsPartitionImageQuestion).                 */
        /*     objExecution : the running command's services.                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : OK with the target and caption set (or a       */
        /*                     request that named its disk already);          */
        /*                     CANCELLED for a choice not made; the device    */
        /*                     list's own failure; REFUSED for no disks, a    */
        /*                     restart's disk no longer listed, or an image   */
        /*                     file whose length cannot be read.              */
        /*--------------------------------------------------------------------*/
        private RSL_clsResult Sys_AskPartitionTarget(RQT_clsRequest objRequest, EXC_clsExecution objExecution)
        {
            RSL_clsResult objResult;
            POA_clsPartitionOpenArgs objOpen;
            PIQ_clsPartitionImageQuestion objAskFile;
            EDV_clsDeviceResult objListed;
            ANS_clsAnswer objAnswer;
            DCH_clsDeviceChoice objPicked;
            string sImage;
            ulong ullBytes;
            int i;

            objOpen = objRequest.RQT_objPayload as POA_clsPartitionOpenArgs;
            if (objOpen != null && objOpen.POA_bAskDevice)
            {   /* Partition Device: the disks listed, and the one to            */
                /* partition asked with the device picker -- or taken from the   */
                /* list as a restart named it -- before the risk warning,        */
                /* which names it.  Listing needs no Administrator, so this      */
                /* runs in a copy without it too, ahead of any restart.          */
                objResult = Sys_Do(STP_enmStep.ListDevices, objRequest, objExecution);
                if (objResult.RSL_iStatus != SYS_OK)
                {   /* The list could not be read (or the command was   */
                    /* stopped): its outcome is the result.             */
                    return(objResult);
                }

                /* The list itself, which a success must carry. */
                objListed = objResult.RSL_objPayload as EDV_clsDeviceResult;
                if (objListed == null || objListed.EDV_lstDisks == null)
                {   /* Success with no list: a failure, not an empty choice. */
                    return(new RSL_clsResult(SYS_INTERNAL, "Device enumeration returned no usable facts.", null));
                }

                if (objListed.EDV_lstDisks.Count == 0)
                {   /* No device at all: refused -- there is no disk to ask   */
                    /* about, so no picker is shown.                          */
                    return(new RSL_clsResult(SYS_REFUSED, "No disks could be found.", null));
                }

                objPicked = null;
                if (objOpen.POA_sDisk.Length != 0)
                {   /* The copy a restart as Administrator started: the disk   */
                    /* the restarting copy chose (and warned about) is found   */
                    /* in this list by its path, with no question, and         */
                    /* described as the picker describes a choice -- so the    */
                    /* window's caption and size come from the disk as it is   */
                    /* now, not from the other copy.                           */
                    for (i = 0; i < objListed.EDV_lstDisks.Count; i++)
                    {   /* One listed disk per iteration. */
                        if (String.Equals(objListed.EDV_lstDisks[i].DEV_sPath, objOpen.POA_sDisk, StringComparison.OrdinalIgnoreCase))
                        {   /* Its path: this is the disk.  The choice is made   */
                            /* from the row, as the picker's own is.             */
                            objPicked = new DCH_clsDeviceChoice(objListed.EDV_lstDisks[i], 0, true);
                            break;
                        }
                    }

                    if (objPicked == null)
                    {   /* Not listed any more (pulled out during the restart,   */
                        /* say): refused rather than asked -- the warning was    */
                        /* given for that disk, never for another.               */
                        return(new RSL_clsResult(SYS_REFUSED, "The disk chosen before the restart as Administrator (" +
                            objOpen.POA_sDisk + ") is no longer present.", null));
                    }
                }
                else
                {   /* Ask: the picker over the main window, with no range;   */
                    /* the choice describes the device as a browse does.      */
                    objAnswer = objExecution.EXC_Ask(QNK_enmQuestionKind.DeviceChoice, 0,
                        new DCQ_clsDeviceQuestion(objListed.EDV_lstDisks, "Partition which disk?", DVF_clsDeviceForm.DVF_RANGE_NONE, 0, true));
                    if (objAnswer != null && objAnswer.ANS_enmKind == ANK_enmAnswerKind.Yes)
                    {   /* Chosen (a guard tests the record's type). */
                        objPicked = objAnswer.ANS_objValue as DCH_clsDeviceChoice;
                    }

                    if (objPicked == null)
                    {   /* Cancelled: no window, nothing read. */
                        return(new RSL_clsResult(SYS_CANCELLED, "No disk was chosen.", null));
                    }
                }

                /* The request becomes the window it describes, in place:   */
                /* the whole device as its target, captioned by its model   */
                /* text and path, with no table-kind question.  The steps   */
                /* and the window's own handler read them from it.          */
                objRequest.RQT_objTarget = objPicked.DCH_objTarget;
                objRequest.RQT_objPayload = new POA_clsPartitionOpenArgs(
                    objPicked.DCH_sName + " (" + objPicked.DCH_objTarget.TGT_sPath + ")", false, false, "");

                /* Chosen: the window's target and caption are set. */
                return(new RSL_clsResult(SYS_OK, "", null));
            }

            objAskFile = objRequest.RQT_objPayload as PIQ_clsPartitionImageQuestion;
            if (objAskFile != null)
            {   /* Partition/Format > Image File as the menu asks it: the disk   */
                /* image asked with Windows' open dialog over the main window,   */
                /* starting in the folder the request carries.                   */
                objAnswer = objExecution.EXC_Ask(QNK_enmQuestionKind.PartitionImageFile, 0, objAskFile.PIQ_sStartFolder);
                sImage = null;
                if (objAnswer != null && objAnswer.ANS_enmKind == ANK_enmAnswerKind.Yes)
                {   /* Chosen: the path is the answer's text. */
                    sImage = objAnswer.ANS_sText;
                }

                if (String.IsNullOrEmpty(sImage))
                {   /* Cancelled (or a Yes without its path): no window,   */
                    /* nothing read.                                       */
                    return(new RSL_clsResult(SYS_CANCELLED, "No image file was chosen.", null));
                }

                /* Recorded on the posted record at once, so the completion     */
                /* remembers its folder whatever follows -- even a length       */
                /* that cannot be read below, which leaves this record as the   */
                /* request's payload.                                           */
                objAskFile.PIQ_sImage = sImage;

                /* The file's length, read here on the worker: the layout read   */
                /* takes it as the disk's length, in 512-byte sectors.  A file   */
                /* that has gone or cannot be read is refused with the           */
                /* framework's reason, before anything open is asked about.      */
                /* An EMPTY file is not refused here: its length of 0 goes on,   */
                /* and the layout read refuses it as shorter than a sector       */
                /* (CommandNativeSteps.cs).                                      */
                try
                {   /* FileInfo's Length throws for a path that is not a   */
                    /* readable file.                                      */
                    ullBytes = (ulong)new FileInfo(sImage).Length;
                }
                catch (Exception exFile)
                {   /* Not readable: refused with the reason; the open   */
                    /* filesystem is untouched.                          */
                    return(new RSL_clsResult(SYS_REFUSED, exFile.Message, null));
                }

                /* The request becomes the window it describes, in place: the   */
                /* whole file as its target -- no range, no device text,        */
                /* 512-byte sectors -- captioned by its name, with no           */
                /* table-kind question.  The steps and the window's own         */
                /* handler read them from it, and the completion reads the      */
                /* target for the file's folder.                                */
                objRequest.RQT_objTarget = new TGT_clsTarget(sImage, 0, 0, "", ullBytes, 512);
                objRequest.RQT_objPayload = new POA_clsPartitionOpenArgs(Path.GetFileName(sImage), false, false, "");

                /* Chosen: the window's target and caption are set. */
                return(new RSL_clsResult(SYS_OK, "", null));
            }

            /* Neither asks: the disk was named when the request was made, so   */
            /* nothing is asked and the request goes on as it is.  (A guard:    */
            /* every OpenPartition the program posts asks its disk.)            */
            return(new RSL_clsResult(SYS_OK, "", null));
        }

        /*--------------------------------------------------------------------*/
        /* Sys_OpenPartitionSequence:                                         */
        /*                                                                    */
        /* Open the partition window over a file or device: the layout read,  */
        /* then partition mode recorded and the window's view sent.  A layout */
        /* that cannot be read opens no window and sets no mode.  It asks     */
        /* nothing: Partition/Format's risk warning comes before anything     */
        /* open is closed (Sys_OpenPartitionOperation), and New Image's new   */
        /* empty file holds nothing to lose.                                  */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest   : the request, its target the whole file or disk  */
        /*                    (nothing is open: the OpenPartition operation   */
        /*                    has closed it, or New Image has just made the   */
        /*                    file).                                          */
        /*     objExecution : the running command's services.                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : the layout read's outcome (its layout the      */
        /*                     payload).                                      */
        /*--------------------------------------------------------------------*/
        private RSL_clsResult Sys_OpenPartitionSequence(RQT_clsRequest objRequest, EXC_clsExecution objExecution)
        {
            RSL_clsResult objResult;

            /* The layout.  The step tells a disk it cannot read from one     */
            /* that simply has no table (which opens with an empty layout);   */
            /* only the first is a failure.                                   */
            objResult = Sys_Do(STP_enmStep.ReadLayout, objRequest, objExecution);
            if (objResult.RSL_iStatus != SYS_OK)
            {   /* Unreadable: no mode and no window. */
                return(objResult);
            }

            /* Partition mode recorded BEFORE the view is sent: the window   */
            /* is modal and its loop can post a button's request at once,    */
            /* which is then held to this mode and this window's id.         */
            Sys_enmMode = MOD_enmMode.Partition;
            Sys_objTarget = objRequest.RQT_objTarget;
            Sys_lModeWindow = objRequest.RQT_lSerial;
            Sys_PublishState(objRequest);
            SYS_Show(new VWM_clsView(VWK_enmViewKind.PartitionOpen, objRequest, objResult));

            /* The layout's result.  The window's requests that follow are   */
            /* held to the window id recorded above.                         */
            return(objResult);
        }

        /*--------------------------------------------------------------------*/
        /* Sys_CloseExclusiveView:                                            */
        /*                                                                    */
        /* Close the check or partition window that owns the mode: the window */
        /* asked (ModeClose -- it may hold unwritten changes, or a running    */
        /* check), then told to close, then waited for until it says it has   */
        /* gone (ModeClosed).  Only then does the mode end.  Which window it  */
        /* is was settled before this is called: the partition window's       */
        /* Close / Open were held to it by the gate (Sys_Denied), and Exit    */
        /* closes whichever window owns the mode.                             */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest : the request closing it.                           */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : OK once the window has gone and the mode has   */
        /*                     ended; CANCELLED when it was declined or did   */
        /*                     not close, the mode then kept.                 */
        /*--------------------------------------------------------------------*/
        private RSL_clsResult Sys_CloseExclusiveView(RQT_clsRequest objRequest)
        {
            ANS_clsAnswer objAnswer;
            long lWindow;
            bool bReopen;
            bool bClosed;
            string sReason;

            /* The window and the check's reopen, kept now.  Nothing can       */
            /* change them during the two questions below: this worker runs    */
            /* no queued notice while it waits, so the window's close notice   */
            /* waits its turn.                                                 */
            lWindow = Sys_lModeWindow;
            bReopen = Sys_bReopenAfterCheck;
            objAnswer = SYS_Ask(QNK_enmQuestionKind.ModeClose, objRequest, lWindow);
            if (objAnswer == null || objAnswer.ANS_enmKind != ANK_enmAnswerKind.Yes)
            {   /* The window said no (or did not answer): it stays open,   */
                /* and nothing that needed it closed follows.               */
                return(new RSL_clsResult(SYS_CANCELLED, "The operation window remains open.", null));
            }

            /* The check's reopen turned off while this close runs, so the    */
            /* CheckClosed it causes does not reopen the old filesystem (an   */
            /* Exit, or the window's own Open, follows instead).  Put back    */
            /* in the finally if the window does not close.                   */
            Sys_bReopenAfterCheck = false;
            bClosed = false;
            try
            {   /* The window told to close -- only now, after its Yes. */
                if (!SYS_Show(new VWM_clsView(VWK_enmViewKind.WindowClose, objRequest, lWindow)))
                {   /* The view could not be sent: that is no evidence the   */
                    /* window has gone, so the mode is kept.                 */
                    return(new RSL_clsResult(SYS_CANCELLED, "The operation window could not be closed.", null));
                }

                /* Waited for, as a question with no dialog: the window      */
                /* answers Closed only once it has really gone and cleaned   */
                /* up, or answers with why it did not.                       */
                objAnswer = SYS_Ask(QNK_enmQuestionKind.ModeClosed, objRequest, lWindow);
                if (objAnswer == null || objAnswer.ANS_enmKind != ANK_enmAnswerKind.Closed)
                {   /* It did not close: the mode is kept, and the caller   */
                    /* (an Exit) stops, the program staying open.           */
                    sReason = "The operation window did not finish closing.";
                    if (objAnswer != null && !String.IsNullOrEmpty(objAnswer.ANS_sText))
                    {   /* The window's own reason, rather than a guess. */
                        sReason = objAnswer.ANS_sText;
                    }

                    /* CANCELLED, with the reason; the user can deal with   */
                    /* the window and try again.                            */
                    return(new RSL_clsResult(SYS_CANCELLED, sReason, null));
                }

                /* Gone: the mode ends, for this window only.  Its own close   */
                /* notice, when it is reached, then finds no mode to end.      */
                bClosed = true;
                Sys_enmMode = MOD_enmMode.None;
                Sys_objTarget = null;
                Sys_lModeWindow = 0;
            }
            finally
            {   /* However this ended: if the window did not close, the   */
                /* check's reopen goes back to what it was.               */
                if (!bClosed)
                {   /* Not closed: the reopen restored. */
                    Sys_bReopenAfterCheck = bReopen;
                }
            }

            /* Closed for real -- not merely told to close -- so the caller   */
            /* may go on.                                                     */
            return(new RSL_clsResult(SYS_OK, "", null));
        }

        /*--------------------------------------------------------------------*/
        /* Sys_PartitionEndSequence:                                          */
        /*                                                                    */
        /* The partition window's Close, or its Open of one partition: the    */
        /* window closed (Sys_CloseExclusiveView), then, for Open, that       */
        /* partition opened read-only and its root listed.  The range opened  */
        /* is the one the request captured at the click, never read back      */
        /* from the window after it has closed.                               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest   : the Close or Open request (its target the       */
        /*                    partition, for Open).                           */
        /*     objExecution : the running command's services.                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : the close's outcome (for Close, or a close     */
        /*                     that failed), the open's failure, or the       */
        /*                     listing's outcome.                             */
        /*--------------------------------------------------------------------*/
        private RSL_clsResult Sys_PartitionEndSequence(RQT_clsRequest objRequest, EXC_clsExecution objExecution)
        {
            RSL_clsResult objResult;
            RQT_clsRequest objOpen;

            /* The window asked and closed, before anything is opened. */
            objResult = Sys_CloseExclusiveView(objRequest);
            if (objResult.RSL_iStatus != SYS_OK || objRequest.RQT_enmKind == KND_enmKind.ClosePartition)
            {   /* A plain Close is done; and a window that did not close   */
                /* opens nothing.                                           */
                return(objResult);
            }

            /* The open, as a request made here (never queued, releasing      */
            /* nothing) on the request's captured range, carrying the outer   */
            /* request's serial so its views reach the running command.  A    */
            /* new epoch: the partition is a new open.                        */
            objOpen = new RQT_clsRequest(KND_enmKind.Open, 0, Sys_lEpoch,
                objRequest.RQT_objTarget, null, 0, 0, null, "", "", "/");
            objOpen.RQT_lSerial = objRequest.RQT_lSerial;
            objResult = Sys_OpenSession(objOpen, objExecution, false, true);
            if (objResult.RSL_iStatus != SYS_OK)
            {   /* Not opened: no listing. */
                return(objResult);
            }

            /* Opened: its root listed, the listing the result. */
            return(Sys_Refresh(objOpen, objExecution));
        }

        /*--------------------------------------------------------------------*/
        /* Sys_ExitSequence:                                                  */
        /*                                                                    */
        /* Exit, or the restart as Administrator (the same Exit carrying the  */
        /* device action that needs it): asked when there is work to lose,    */
        /* then everything closed in order -- the queued work cancelled, the  */
        /* inputs, every editor (each asked to save), the session, the check  */
        /* or partition window -- and for a restart, the elevated copy        */
        /* started.  Any refusal on the way stops the Exit with the program   */
        /* still running; what was already closed stays closed.               */
        /*                                                                    */
        /* Arguments:                                                         */
        /*     objRequest   : the Exit request (its payload the restart's     */
        /*                    arguments, for a restart).                      */
        /*     objExecution : the running command's services.                 */
        /*                                                                    */
        /* Returns:                                                           */
        /*     RSL_clsResult : OK when everything has closed (and the         */
        /*                     restart, if any, was launched) -- the worker   */
        /*                     then stops; otherwise why the Exit stopped.    */
        /*--------------------------------------------------------------------*/
        private RSL_clsResult Sys_ExitSequence(RQT_clsRequest objRequest, EXC_clsExecution objExecution)
        {
            RSL_clsResult objResult;
            ANS_clsAnswer objAnswer;
            ELA_clsElevationArgs objElevation;
            bool bNeedsConsent;
            string sReason;

            /* Whether to ask, decided under the queue lock: not when an        */
            /* Exit was already agreed to while a command ran (the program is   */
            /* already terminal); otherwise when anything could be lost -- a    */
            /* restart, requests waiting, a command that was running when the   */
            /* Exit came, an input open.  The Exit's flags are cleared here,    */
            /* as this is the Exit that answers them.                           */
            objElevation = objRequest.RQT_objPayload as ELA_clsElevationArgs;
            lock (Sys_objQueue)
            {   /* Read and clear together, so a post between cannot slip   */
                /* in unseen.                                               */
                bNeedsConsent = !Sys_bTerminal && (objElevation != null ||
                    Sys_lstWaiting.Count != 0 || Sys_bExitHadRunning || Sys_lstInputs.Count != 0);
                Sys_bExitPending = false;
                Sys_bExitHadRunning = false;
            }

            if (bNeedsConsent && !Sys_Yes(QNK_enmQuestionKind.ExitRunning, objRequest, 0))
            {   /* Asked, and not Yes: the program keeps running, and the   */
                /* waiting requests stay queued.                            */
                return(new RSL_clsResult(SYS_CANCELLED, "Exit declined.", null));
            }

            lock (Sys_objQueue)
            {   /* Terminal from now on: no new ordinary request is taken   */
                /* while the editors are asked and closed.                  */
                Sys_bTerminal = true;
            }

            /* The terminal state published before anything is closed, so   */
            /* the windows grey their commands while the worker does the    */
            /* closing in its own order.                                    */
            Sys_PublishState(objRequest);

            /* The requests still waiting cancelled -- an editor's queued       */
            /* save among them is answered, so its window is not left waiting   */
            /* when it is asked to close.                                       */
            Sys_CancelPending();

            /* An editor sent to the UI may still be being made (inside its    */
            /* own nested UI) and not yet registered: waited for, so none is   */
            /* missed; their registrations are applied in the loop below.      */
            objAnswer = SYS_Ask(QNK_enmQuestionKind.EditorsReady, objRequest, 0);
            if (objAnswer == null || objAnswer.ANS_enmKind != ANK_enmAnswerKind.Closed)
            {   /* No such answer: which editors exist is not known, so the   */
                /* Exit stops here.                                           */
                return(new RSL_clsResult(SYS_CANCELLED, "Viewer creation did not finish.", null));
            }

            /* Every input closed, even with no command running (an input    */
            /* can be open while the worker is idle), before any editor it   */
            /* may disable is asked about its unsaved work.                  */
            objResult = Sys_CloseInputSet(objRequest, 0, false);
            if (objResult.RSL_iStatus != SYS_OK)
            {   /* An input would not close: the Exit stops before any   */
                /* editor or the session.                                */
                return(objResult);
            }

            /* Both editor sets, again and again until both are empty: a    */
            /* Save As can move an editor from one set to the other while   */
            /* another is being asked.                                      */
            for (;;)
            {   /* One pass: the Windows-file editors, then the image's.      */
                /* The facts the windows posted (registrations, saves made)   */
                /* applied first -- they decide the sets, and are not work    */
                /* to run.                                                    */
                Sys_FlushEditorBindings(objRequest);
                if (Sys_dictHostEditors.Count == 0 && Sys_lstEditors.Count == 0)
                {   /* Both empty: no editor left anywhere. */
                    break;
                }

                /* The Windows-file editors, each asked; their read-out files   */
                /* are released only as each one closes.                        */
                objResult = Sys_CloseEditors(objRequest, objExecution, true, true);
                if (objResult.RSL_iStatus != SYS_OK)
                {   /* One Cancel: the whole Exit stops. */
                    return(objResult);
                }

                /* The image's editors.  One moved to Windows by a Save As is   */
                /* closed by the next pass.                                     */
                objResult = Sys_CloseEditors(objRequest, objExecution, false, true);
                if (objResult.RSL_iStatus != SYS_OK)
                {   /* A Cancel or a failed save: the Exit stops, nothing   */
                    /* more is closed.                                      */
                    return(objResult);
                }
            }

            /* The session closed (and any inputs and editors left settled)   */
            /* with the ordinary close, no question.                          */
            objResult = Sys_PrepareTransition(objRequest, objExecution, false);
            if (objResult.RSL_iStatus != SYS_OK)
            {   /* The close failed: the Exit stops here. */
                return(objResult);
            }

            if (Sys_enmMode == MOD_enmMode.Check || Sys_enmMode == MOD_enmMode.Partition)
            {   /* The check or partition window still open: asked and       */
                /* closed (it may hold unwritten changes), and waited for.   */
                objResult = Sys_CloseExclusiveView(objRequest);
                if (objResult.RSL_iStatus != SYS_OK)
                {   /* Declined, or it did not close: the Exit stops. */
                    return(objResult);
                }
            }

            if (objElevation != null)
            {   /* The restart, reached only once every editor, input,        */
                /* session and window has gone.  The UI starts the elevated   */
                /* copy and answers through a question (StartElevated) once   */
                /* Windows' prompt has returned; nothing else can run         */
                /* meanwhile, the program being terminal.                     */
                objAnswer = SYS_Ask(QNK_enmQuestionKind.StartElevated, objRequest, 0);
                if (objAnswer == null || objAnswer.ANS_enmKind != ANK_enmAnswerKind.Yes)
                {   /* Declined in Windows' prompt, or the window gone: this   */
                    /* copy keeps running.  The scheduler's failed-Exit path   */
                    /* lets requests in again; what was closed stays closed,   */
                    /* as the message says.                                    */
                    sReason = "The restart as Administrator did not happen.";
                    if (objAnswer != null && !String.IsNullOrEmpty(objAnswer.ANS_sText))
                    {   /* Windows' own reason added, rather than a bare   */
                        /* Cancel.                                         */
                        sReason += " " + objAnswer.ANS_sText;
                    }

                    /* CANCELLED, with the reason. */
                    return(new RSL_clsResult(SYS_CANCELLED,
                        sReason + " Editors and the filesystem remain closed.", null));
                }
            }

            /* Everything closed (and any restart launched).  The scheduler   */
            /* posts Stopped after this result, once the queue is cleared.    */
            return(new RSL_clsResult(SYS_OK, "", null));
        }
    }

}
