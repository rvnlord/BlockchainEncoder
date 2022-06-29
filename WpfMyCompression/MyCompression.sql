DELETE FROM RawBlocks;
SELECT * FROM RawBlocks;
SELECT COUNT(*) FROM RawBlocks;

SELECT * FROM RawBlocks WHERE "Index" > 53000;

UPDATE RawBlocks SET ExpandedBlockHash = null WHERE Index = 2;