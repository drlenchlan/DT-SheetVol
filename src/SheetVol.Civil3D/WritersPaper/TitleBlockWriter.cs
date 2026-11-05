// A3 work package. Owner: A3.
//
// Title-block attribute fill by tag map (HANDOFF.md 6.7, E615). Every block reference on
// the layout that carries attributes is searched (tables and the table anchor are
// skipped); a configured tag found there gets its value, a tag found nowhere is skipped
// with an Info note. Values are compared before writing so an unchanged regen writes
// nothing (E811). A reference that received values carries an owner tag (role
// TitleAttributes, qualifier = its block name).

using Autodesk.AutoCAD.DatabaseServices;
using SheetVol.Core;
using SheetVol.Core.Tracking;

namespace SheetVol.Civil3D.WritersPaper;

internal sealed class TitleBlockWriter(PaperConfig config)
{
    /// <summary>
    /// Fills <paramref name="values"/> (tag to text) on the layout. Returns Created when the
    /// first values were written to a reference, Updated when values changed, Unchanged, or
    /// None when the map is empty.
    /// </summary>
    internal PaperChange Apply(Transaction tr, Database db, BlockTableRecord layoutBlock, IReadOnlyDictionary<string, string> values,
                               string anchorBlockName, Guid runId, Guid segmentId, WriteCounter writes, List<Problem> problems,
                               string layoutName, List<string> handles)
    {
        if (values.Count == 0) return PaperChange.None;
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var constant = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var change = PaperChange.None;

        foreach (ObjectId id in layoutBlock)
        {
            if (id.IsErased || !id.ObjectClass.IsDerivedFrom(Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(BlockReference)))) continue;
            if (tr.GetObject(id, OpenMode.ForRead) is not BlockReference br || br is Table || br.AttributeCollection.Count == 0) continue;
            string blockName = PaperTemplate.EffectiveName(tr, br);
            if (string.Equals(blockName, anchorBlockName, StringComparison.OrdinalIgnoreCase)) continue;

            var written = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bool changedHere = false;
            foreach (ObjectId attId in br.AttributeCollection)
            {
                if (attId.IsErased) continue;
                var att = (AttributeReference)tr.GetObject(attId, OpenMode.ForRead);
                if (!values.TryGetValue(att.Tag, out var value)) continue;
                if (att.IsConstant) { constant.Add(att.Tag); continue; }
                found.Add(att.Tag);
                written[att.Tag] = value;
                if (att.TextString == value) continue;
                att = OwnerTags.OpenForWrite<AttributeReference>(tr, attId);
                writes.Add();
                att.TextString = value;
                if (att.Justify != AttachmentPoint.BaseLeft) att.AdjustAlignment(db);
                changedHere = true;
            }
            if (written.Count == 0) continue;

            handles.Add(br.Handle.ToString());
            // Block names run to 255 characters; an owner-tag field holds 250 (6.9).
            string qualifier = blockName.Length <= 200 ? blockName : "sha256:" + CanonicalWriter.Hash(blockName);
            var tag = new OwnerTag(runId, segmentId, OwnedRole.TitleAttributes, qualifier,
                ContentSignatures.OfTexts(written.Select(kv => $"{kv.Key}={kv.Value}")), []);
            bool hadTag = OwnerTags.Read(tr, br, config.OwnerTagKey) is not null;
            if (!OwnerTags.Matches(tr, br, config.OwnerTagKey, tag))
            {
                var w = OwnerTags.OpenForWrite<BlockReference>(tr, id);
                writes.Add();
                OwnerTags.Write(tr, w, config.OwnerTagKey, tag);
                changedHere = true;
            }
            if (changedHere)
                change = !hadTag ? PaperChange.Created : change == PaperChange.Created ? PaperChange.Created : PaperChange.Updated;
            else if (change == PaperChange.None)
                change = PaperChange.Unchanged;
        }

        foreach (var tag in values.Keys.Where(k => !found.Contains(k)))
            problems.Add(constant.Contains(tag)
                ? new Problem(PaperCodes.TitleTagMissing, Severity.Info,
                    $"Title-block attribute '{tag}' on layout '{layoutName}' is a constant attribute and cannot be filled; skipped.", segmentId,
                    $"Make '{tag}' a normal attribute in the title block definition.")
                : new Problem(PaperCodes.TitleTagMissing, Severity.Info,
                    $"Title-block attribute tag '{tag}' is not on layout '{layoutName}'; skipped.", segmentId,
                    $"Add an attribute tagged '{tag}' to the title block, or remove it from the titleAttributes map."));
        return change;
    }
}
