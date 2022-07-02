--DELETE FROM RawBlocks;
SELECT * FROM RawBlocks;
SELECT COUNT(*) FROM RawBlocks;

SELECT * FROM RawBlocks rb WHERE rb."Index" > 65500;

--UPDATE RawBlocks SET ExpandedBlockHash = null;