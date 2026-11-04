# Seed levels

How per-crop seed levels change growth time, sell price, harvest count and seed price. Compiled from screenshots of the
rice seed at Lv 1 to 5 and from the "at Lv N: ..." previews shown on each screen. Values for Lv 6 and Lv 7 are read from
those previews, and the seed price at Lv 6 and Lv 7 is an estimate. See [World map](world-map.md#seeds-たね) for base
prices and unlock levels.

## Effects per level

| Lv | Growth time | Sell price | Harvest count | Seed price | Requirement to reach it |
| --- | --- | --- | --- | --- | --- |
| 1 | base | base | base | base | Available from the start |
| 2 | -10% | base | base | base | Harvest count (for example 2 melons, 3 rice) |
| 3 | -10% | +20% | base | +10% | 2,500 coins |
| 4 | -10% | +20% | +1 with 33% chance | +10% | 5,000 coins |
| 5 | -20% | +40% | +1 with 33% chance | +20% | 10,000 coins |
| 6 | -30% | +60% | +1 with 33% chance | +30% (estimated) | Harvest count (rice: 14 more after Lv 5) |
| 7 | -50% | +100% | always +2 | +50% (estimated) | Player level 60 |

## How the numbers combine

- **Percentages apply to the base value, not to each other.** Rice at Lv 5 grows in 70 min x 0.8 = 56 min and sells for
  100 x 1.4 = 140. Lv 6 and Lv 7 work the same way.
- **The values shown in the shop already include the current level.** To get the base price back from a Lv 6 seed,
  divide by 1.6: a potato shown at 72 has a base of 72 / 1.6 = 45.
- **Each level replaces the previous one.** Growth time goes -10%, -20%, -30%, -50%; it is never -10% plus -20%.
- **There are three kinds of requirement.** Lv 2 and Lv 6 need a number of harvested crops, Lv 3 to 5 need coins, and
  only Lv 7 needs a player level.
- **Seed price rises with the level**, by roughly half of the sell price bonus.

## Example: rice

| Lv | Growth time | Sell price |
| --- | --- | --- |
| 1 | 70 min | 100 |
| 2 to 4 | 63 min | 100 at Lv 2, 120 at Lv 3 and 4 |
| 5 | 56 min | 140 |
| 6 (predicted) | 49 min | 160 |
| 7 (predicted) | 35 min | 200 |

## Unknowns

- **How Lv 7 "always +2" relates to Lv 4 "+1 with 33% chance".** The screens do not say whether they add up or whether
  Lv 7 replaces the chance.
- **Seed price at Lv 6 and Lv 7.** Unconfirmed; the shop will show it once a seed is actually upgraded.
- **Upgrade coins for crops other than rice.** The coin costs above were read for rice and may differ per crop. With the
  melon costs for Lv 2 to 5, the payback time of an upgrade could be computed.
