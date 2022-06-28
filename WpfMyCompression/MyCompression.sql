DELETE FROM RawBlocks;
SELECT * FROM RawBlocks;
SELECT COUNT(*) FROM RawBlocks;

SELECT * FROM RawBlocks WHERE "Index" > 47900;

UPDATE RawBlocks SET ExpandedBlockHash = null WHERE Index = 2;