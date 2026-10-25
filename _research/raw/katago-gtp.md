## KataGo GTP Extensions

In addition to a basic set of [GTP commands](https://www.lysator.liu.se/~gunnar/gtp/), KataGo supports a few additional commands:

   * `rectangular_boardsize X Y`
      * Sets the board size to a potentially non-square size, width `X` and height `Y`. Some of KataGo's older nets were not trained with non-square sizes, but often seem to generalize to them pretty well. More recent nets are trained on a very low frequency of non-square sizes.
      * KataGo will *also* accept the same two arguments X Y for the standard GTP `boardsize` command to set a rectangular size, in addition to the normal single-argument usage of `boardsize` to set a square size.
   * `set_position COLOR VERTEX COLOR VERTEX COLOR VERTEX ...`
      * Directly specify an initial board position as a sequence of color-vertex pairs, replacing the current board.
      * The newly-set position is assumed to have no move history. Therefore:
         * There are no ko or superko prohibitions yet in the specified position.
         * If using any territory scoring rules, it is assumed that there are no captures so far.
      * Part of the reason for this command is because modern neural-net-based bots, KataGo included, may mildly bias their move suggestions based on the recent moves. This command provides a way to set initial board positions (e.g. whole-board tsumego) without using successive `play` commands which might imply a ridiculous move history.
      * It is NOT recommended to use this command to place the starting stones for handicap games. Use the standard GTP commands `fixed_handicap`, `place_free_handicap`, and/or `set_free_handicap` instead.
      * Calling `set_position` with zero arguments is equivalent to calling `clear_board`.
      * Fails, reports a normal GTP error message, and leaves the board state unchanged if any vertex is specified more than once or if the final configuration would contain stones with zero liberties.
   * `clear_cache`
      * Clears the search tree and the NN cache. Can be used to force KataGo to re-search a position freshly, re-randomizing the search on that position, or to free up memory.
   * `stop`
      * Halts any ongoing pondering, if pondering was enabled in the gtp config.
   * `get_komi`
      * Report what komi KataGo is currently set to use.
   * `kata-get-rules`
      * Returns a JSON dictionary indicating the current rules KataGo is using.
      * For example: `{"hasButton":false,"ko":"POSITIONAL","scoring":"AREA","suicide":true,"tax":"NONE","whiteHandicapBonus":"N-1","friendlyPassOk":true}`
      * See https://lightvector.github.io/KataGo/rules.html for a detailed description of the rules implemented.
      * Explanation of individual fields:
         * `ko: ("SIMPLE" | "POSITIONAL" | "SITUATIONAL")` - The rule used for preventing cycles.
         * `scoring: ("AREA" | "TERRITORY")` - The rule used for computing the score of the game.
         * `tax: ("NONE" | "SEKI" | "ALL")` - Modification to the scoring rule, indicating whether territory in SEKI is taxed, or whether ALL groups pay a tax of up to 2 points for eyes.
         * `suicide: (true | false)` - Whether multi-stone suicide is legal.
         * `hasButton: (true | false)` - Whether [button Go](https://senseis.xmp.net/?ButtonGo) is being used.
         * `whiteHandicapBonus ("0" | "N-1" | "N")` - In handicap games, whether white gets 0, N-1, or N bonus points, where N is the number of black handicap stones.
         * `friendlyPassOk: (true | false)` - When false, indicates that in this ruleset a player should capture all dead stones before passing.
      * **NOTE: It is possible that more fields and more options for these fields will be added in the future.**
   * `kata-set-rules RULES`
      * Sets the current rules KataGo should be using. Does NOT otherwise affect the board position.
      * `RULES` should either be a JSON dictionary in the same format of `kata-get-rules`, or be a shorthand string like `tromp-taylor`. Some possible shorthand strings are:
         * `tromp-taylor  : Equivalent to {"hasButton":false,"ko":"POSITIONAL", "scoring":"AREA",     "suicide":true, "tax":"NONE","whiteHandicapBonus":"0","friendlyPassOk":false}`
         * `chinese       : Equivalent to {"hasButton":false,"ko":"SIMPLE",     "scoring":"AREA",     "suicide":false,"tax":"NONE","whiteHandicapBonus":"N","friendlyPassOk":true}`
         * `chinese-ogs   : Equivalent to {"hasButton":false,"ko":"POSITIONAL", "scoring":"AREA",     "suicide":false,"tax":"NONE","whiteHandicapBonus":"N","friendlyPassOk":true}`
         * `chinese-kgs   : Equivalent to {"hasButton":false,"ko":"POSITIONAL", "scoring":"AREA",     "suicide":false,"tax":"NONE","whiteHandicapBonus":"N","friendlyPassOk":true}`
         * `japanese      : Equivalent to {"hasButton":false,"ko":"SIMPLE",     "scoring":"TERRITORY","suicide":false,"tax":"SEKI","whiteHandicapBonus":"0","friendlyPassOk":true}`
         * `korean        : Equivalent to {"hasButton":false,"ko":"SIMPLE",     "scoring":"TERRITORY","suicide":false,"tax":"SEKI","whiteHandicapBonus":"0","friendlyPassOk":true}`
         * `stone-scoring : Equivalent to {"hasButton":false,"ko":"SIMPLE",     "scoring":"AREA",     "suicide":false,"tax":"ALL", "whiteHandicapBonus":"0","friendlyPassOk":true}`
         * `aga           : Equivalent to {"hasButton":false,"ko":"SITUATIONAL","scoring":"AREA",     "suicide":false,"tax":"NONE","whiteHandicapBonus":"N-1","friendlyPassOk":true}`
         * `bga           : Equivalent to {"hasButton":false,"ko":"SITUATIONAL","scoring":"AREA",     "suicide":false,"tax":"NONE","whiteHandicapBonus":"N-1","friendlyPassOk":true}`
         * `new-zealand   : Equivalent to {"hasButton":false,"ko":"SITUATIONAL","scoring":"AREA",     "suicide":true, "tax":"NONE","whiteHandicapBonus":"0","friendlyPassOk":true}`
         * `aga-button    : Equivalent to {"hasButton":true, "ko":"SITUATIONAL","scoring":"AREA",     "suicide":false,"tax":"NONE","whiteHandicapBonus":"N-1","friendlyPassOk":true}`
      * KataGo does NOT claim that the above rules are _exactly_ a match. These are merely the _closest_ settings that KataGo has to those countries' rulesets.
      * A small number of combinations are currently not supported by even the latest neural nets, for example `scoring TERRITORY` and `hasButton true`.
      * Older neural nets for KataGo (nets released before v1.3) will also not support many of the options, and setting these rules will fail if these neural nets are being used.
      * Note the distinction between the `chinese` and `chinese-ogs, chinese-kgs`. Often in Chinese tournaments, contrary to their nominal written rules, which specify positional superko, only simple ko is used, and triple ko typically does result in the referree voiding the game. However, many servers have implemented the nominal written rules rather than the actually-used rules.
   * `kata-set-rule RULE VALUE`
      * Sets a single field of the current rules, leaving other fields unaffected.
      * For example, `kata-set-rule ko SIMPLE`.
      * May fail, if setting this field would result in a combination of rules that is not supported by the current neural net.
   * `kgs-rules RULES`
      * This is an extension for playing on KGS, via kgsGtp.
      * As specified by kgsGtp docs, `RULES` should be one of `chinese | aga | japanese | new_zealand`.
      * Has the same behavior as `kata-set-rules` except that `chinese` maps to `chinese-kgs` above.
   * `kgs-time_settings KIND ...`
      * This is an extension for playing on KGS, via kgsGtp.
      * It is not clear if other implementations will support floating-point values for times, however, KataGo does support them. Values must still be non-negative and finite, and values up to at least 2^31-1 are supported.
      * As specified by kgsGtp docs, `KIND` should be one of `none | absolute | canadian | byoyomi`.
         * `none` indicates no time control
         * `absolute` should be followed by a single float `MAINTIME` specifying the time in seconds.
         * `canadian` should be followed by `MAINTIME BYOYOMITIME BYOYOMISTONES` (float,float,int) specifying the main time, the length of the canadian overtime period, and the number of stones that must be played in that period.
         * `byoyomi` should be followed by `MAINTIME BYOYOMITIME BYOYOMIPERIODS` (float,float,int) specifying the main time, the length of each byo-yomi period, and the number of byo-yomi periods.
      * NOTE: As a hack, KGS also expects that when using `byoyomi`, the controller should provide the number of periods left in place of the number of stones left for the GTP `time_left` command.
   * `kata-time_settings KIND ...`
      * This is an extension for exposing KataGo's handling of more time controls besides those on KGS.
      * KataGo and any bots that also choose to implement this same extension must support floating-point values for times, as well as for the normal `time_left` command. Values must still be non-negative and finite, and values up to at least 2^31-1 must be supported.
      * Supports all of the same settings as `kgs-time_settings` except includes additional time settings:
        * `fischer` should be followed by two floats `MAINTIME INCREMENT` specifying the original main time in seconds and the number of seconds that is added to a player's clock at the end of making each move. And just as with `absolute`, when using this time setting the controller must always report `0` for the number of stones in the `time_left` command.
        * `fischer-capped` should be followed by four floats `MAINTIME INCREMENT MAINTIMELIMIT MAXTIMEPERMOVE`. Same as `fischer` except:
          * `MAINTIMELIMIT` is a cap such that after adding the increment, if the main time is now larger than the limit, it is reduced to the limit. If this limit is used it must be `>= MAINTIME`.
          * `MAXTIMEPERMOVE` is a limit on the maximum time that may be spent on any individual move.
          * Setting either of these two floats to a negative value such as `-1` indicates that the value is unused and there is no such limit.
      * Additionally, unlike `time_settings` or `kgs-time_settings` which demand integer values, for `kata-time_settings`, floating point values are explicitly allowed for all times, periods, or increments. Values must be finite and non-negative. An implementation should continue to support values at least up to 2^31-1.
      * More time settings might be added in the future.
   * `kata-list_time_settings`
      * Reports all time settings supported by `kata-time_settings`, separated by whitespace. Currently the list is `none absolute byo-yomi canadian fischer`.
      * GTP controllers that use `kata-time_settings` are advised to use this command to check if their time setting is supported.
   * `lz-analyze [player (optional)] [interval (optional)] KEYVALUEPAIR KEYVALUEPAIR ...`
      * Begin searching and optionally outputting live analysis to stdout. Assumes the normal player to move next unless otherwise specified.
      * Possible key-value pairs:
         * `interval CENTISECONDS` - Output a line every this many centiseconds. Alternate way to specify interval besides as the second argument.
         * `minmoves N` - Output stats for at least N different legal moves if possible (will likely cause KataGo to output stats on 0-visit moves)
         * `maxmoves N` - Output stats for at most N different legal moves (NOTE: Leela Zero does NOT currently support this field)
         * `avoid PLAYER VERTEX,VERTEX,... UNTILDEPTH` - Prohibit the search from exploring the specified moves for the specified player, until `UNTILDEPTH` ply deep in the search.
            * May be supplied multiple times with different `UNTILDEPTH` for different sets of moves. The behavior is unspecified if a move is specified more than once with different `UNTILDEPTH`.
         * `allow PLAYER VERTEX,VERTEX,... UNTILDEPTH` - Equivalent to `avoid` on all vertices EXCEPT for the specified vertices. Can only be specified once, and cannot be specified at the same time as `avoid`.
         * `focus VERTEX[:WEIGHT],VERTEX[:WEIGHT],... PROB` - Direct extra search into the specified moves for the player to move. Each playout from the root is redirected with probability `PROB` into one of the specified moves, as a weightless visit that does not count toward the root's own visits or value. `PROB` is a probability from 0 to 1. It is capped at 0.99 so that the root keeps receiving some visits and visit limits are still reached. The move is chosen at random in proportion to the weights. Weights are positive numbers and default to 1 when omitted. For example `focus C3:3,D4 0.5` sends three quarters of the focused playouts to C3 and one quarter to D4. Such visits also do not count toward visit or playout limits, so focusing adds search on top of the normal search rather than replacing part of it. The reported `visits` of a focused move include these extra visits. Moves that are illegal or excluded by `avoid` or `allow` are ignored. Can only be specified once.
            * To change or cancel the focus while analyzing, send the analyze command again with a different `focus` or without one. If the other arguments are unchanged, the search tree is kept across such re-sends, so no search progress is lost.
      * Output format:
         * Outputted lines look like `info move E4 visits 1178 winrate 4802 prior 2211 lcb 4781 order 0 pv E4 E3 F3 D3 F4 P4 P3 O3 Q3 O4 K3 Q6 S6 E16 E17 info move P16 visits 1056 winrate 4796 prior 2206 lcb 4769 order 1 pv P16 P17 O17 Q17 O16 E16 E17 F17 D17 F16 K17 D14 B14 P3 info move E3 visits 264 winrate 4752 prior 944 lcb 4722 order 2 pv E3 D5 P16 P17 O17 Q17 O16 E17 H17 D15 C15 D14 info move E16 visits 262 winrate 4741 prior 1047 lcb 4709 order 3 pv E16 P4 P3 O3 Q3 O4 P16 P17 O17 Q17 O16 Q14`
         * `info` - Indicates the start of information for a new possible move
         * `move` - The move being analyzed.
         * `visits` - The number of visits invested into the move so far.
         * `winrate` - 10000 times the winrate of the move so far, rounded to an integer in [0,10000].
         * `prior` - 10000 times the policy prior of the move, rounded to an integer in [0,10000].
         * `lcb` - 10000 times the [LCB](https://github.com/leela-zero/leela-zero/issues/2282) of the move, rounded to an integer in [0,10000], or possibly a value outside that interval, since the current implementation doesn't strictly account for the 0-1 bounds.
         * `order` - KataGo's ranking of the move. 0 is the best, 1 is the next best, and so on.
         * `pv` - The principal variation following this move. May be of variable length or even empty.
      * All output values are from the perspective of the current player, unless otherwise configured in KataGo's gtp config.
      * This command is a bit unusual for GTP in that it will run forever on its own, but asynchronously if any new GTP command or a raw new

... [truncated: total 39109 chars, use -Skip 15000]
