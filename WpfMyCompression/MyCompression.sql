--DELETE FROM RawBlocks;
SELECT * FROM RawBlocks;
SELECT COUNT(*) FROM RawBlocks;

SELECT * FROM RawBlocks rb WHERE rb."Index" = 6314;

UPDATE RawBlocks SET ExpandedBlockHash = null;