; AutoCAD 2021: load after stage3-selection.lsp in a NEW drawing.
; These commands only READ buffers. Edit the palette and run U/REDO yourself.
; Record the actual AutoCAD version when reporting results.
(vl-load-com)

(defun xdata-stage5-fail (message)
  (princ (strcat "\nFAIL: " message))
  (exit))

(defun xdata-stage5-records (dictionary path depth / entry entries object values result count)
  (setq *xdata-stage5-dictionaries* (1+ *xdata-stage5-dictionaries*))
  (if (> *xdata-stage5-dictionaries* 256) (xdata-stage5-fail "Too many dictionaries in this fixture."))
  (if (> depth 32) (xdata-stage5-fail "Dictionary depth exceeds this fixture's limit."))
  ; dictnext has one global iterator: collect this level before entering nested dictionaries.
  (setq entry (dictnext dictionary T) count 0)
  (while entry
    (setq entries (cons entry entries) count (1+ count))
    (if (> count 256) (xdata-stage5-fail "Dictionary exceeds this fixture's limit."))
    (setq entry (dictnext dictionary)))
  (foreach entry (reverse entries)
    (setq object (cdr (assoc -1 entry)))
    (setq values (entget object))
    (cond
      ((= (cdr (assoc 0 values)) "XRECORD")
       (setq result (cons (list (append path (list (cdr (assoc 3 entry)))) values) result)))
      ((= (cdr (assoc 0 values)) "DICTIONARY")
       (setq result (append (xdata-stage5-records object
         (append path (list (cdr (assoc 3 entry)))) (1+ depth)) result)))))
  result)

(defun xdata-stage5-snapshot (handles / handle entity values dictionary result)
  (setq *xdata-stage5-dictionaries* 0)
  (foreach handle handles
    (setq entity (handent handle))
    (if (null entity) (xdata-stage5-fail "A fixture entity is missing."))
    (setq values (entget entity '("*")))
    (setq dictionary (cdr (assoc 360 values)))
    (setq result (cons (list handle values
      (if dictionary (xdata-stage5-records dictionary nil 0) nil)) result)))
  result)

(defun c:XDATASTAGE5BEFORE (/ selection index)
  (setq selection (ssget "_I"))
  (if (or (null selection) (> (sslength selection) 16))
    (xdata-stage5-fail "Select the two fixture entities first (maximum 16)."))
  (setq *xdata-stage5-handles* nil index 0)
  (repeat (sslength selection)
    (setq *xdata-stage5-handles* (cons (cdr (assoc 5 (entget (ssname selection index)))) *xdata-stage5-handles*))
    (setq index (1+ index)))
  (setq *xdata-stage5-before* (xdata-stage5-snapshot *xdata-stage5-handles*))
  (setq *xdata-stage5-after* nil)
  (princ "\nBaseline captured. Change ONE palette field and confirm with Enter.")
  (princ))

(defun c:XDATASTAGE5AFTER ()
  (if (null *xdata-stage5-before*) (xdata-stage5-fail "Run XDATASTAGE5BEFORE first."))
  (setq *xdata-stage5-after* (xdata-stage5-snapshot *xdata-stage5-handles*))
  (if (equal *xdata-stage5-before* *xdata-stage5-after*) (xdata-stage5-fail "No DWG data changed."))
  (princ "\nChanged buffers captured. Run U once, then XDATASTAGE5CHECKUNDO.")
  (princ))

(defun c:XDATASTAGE5CHECKUNDO ()
  (if (null *xdata-stage5-after*) (xdata-stage5-fail "Run XDATASTAGE5AFTER first."))
  (if (not (equal *xdata-stage5-before* (xdata-stage5-snapshot *xdata-stage5-handles*)))
    (xdata-stage5-fail "FAIL: Undo did not restore all entity/XRecord buffers."))
  (princ "\nPASS: whole group restored. Run REDO, then XDATASTAGE5CHECKREDO.")
  (princ))

(defun c:XDATASTAGE5CHECKREDO ()
  (if (null *xdata-stage5-after*) (xdata-stage5-fail "Run XDATASTAGE5AFTER first."))
  (if (not (equal *xdata-stage5-after* (xdata-stage5-snapshot *xdata-stage5-handles*)))
    (xdata-stage5-fail "FAIL: Redo did not restore all changed entity/XRecord buffers."))
  (princ "\nPASS: whole group reapplied and read-only checks preserved Redo.")
  (princ))
(princ)
