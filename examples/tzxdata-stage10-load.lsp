;;; TZXDATA stage 10: load test of the palette in AutoCAD 2021 for docs/autocad-verification.md.
;;; Import examples/tzxdata-stage10-load-schema.json with VELEC_SCHEMA_IMPORT first, set XDATAPALETTE_TRACE=1
;;; before starting AutoCAD to see the times, load this file with APPLOAD and run VELECLOAD:
;;; 1000 LINE objects of the template LOAD get 50 parameters each and are selected.
;;; VELECLOADSELECT selects the same objects again (the palette must not parse XDATA again);
;;; VELECLOADTOUCH changes object 7 outside the palette (only it is parsed again).
;;; The values are the same as in tests/AcadPaletteSetXData.Core.Tests/LoadData.cs.

(defun velec:loadvalue (n i / kind)
  (setq kind (rem (1- i) 5))
  (cond
    ((= kind 0) (cons 1000 (strcat "Щит-" (itoa (rem n 3)))))
    ((= kind 1) (cons 1040 (* n 0.5)))
    ((= kind 2) (cons 1071 i))
    ((= kind 3) (cons 1070 (rem n 2)))
    (t (cons 1000 (substr "ABC" (1+ (rem n 3)) 1)))))

(defun velec:loadid (i)
  (strcat "L" (if (< i 10) "0" "") (itoa i)))

;; XDATA BobrovXDATA v2 of object n: L01…L48, the formula L49 is calculated, the table L50 has rows M0 and M1.
(defun velec:loaddata (n / data i)
  (setq data '((1000 . "LOAD") (1000 . "TEMPLATE") (1070 . 2)) i 1)
  (while (<= i 48)
    (setq data (cons (velec:loadvalue n i) (cons (cons 1000 (velec:loadid i)) data)) i (1+ i)))
  (append (reverse data)
    '((1000 . "L50") (1002 . "{")
      (1002 . "{") (1000 . "LMat_Mark") (1000 . "M0") (1000 . "LMat_Count") (1040 . 1.0) (1002 . "}")
      (1002 . "{") (1000 . "LMat_Mark") (1000 . "M1") (1000 . "LMat_Count") (1040 . 2.0) (1002 . "}")
      (1002 . "}"))))

;; DATA with the value of ID replaced by VALUE.
(defun velec:setvalue (data id value / result)
  (while data
    (setq result (cons (car data) result))
    (if (equal (car data) (cons 1000 id))
      (setq data (cdr data) result (cons value result)))
    (setq data (cdr data)))
  (reverse result))

(defun c:VELECLOAD (/ n x y start)
  (regapp "BobrovXDATA")
  (setq start (getvar "MILLISECS") velec:load (ssadd) n 0)
  (while (< n 1000)
    (setq x (* 12.0 (rem n 40)) y (* -5.0 (/ n 40)))
    (ssadd (entmakex
             (list '(0 . "LINE") (list 10 x y 0.0) (list 11 (+ x 10.0) y 0.0)
               (list -3 (cons "BobrovXDATA" (velec:loaddata n)))))
           velec:load)
    (setq n (1+ n)))
  (princ (strcat "\nСоздано объектов LOAD: " (itoa (sslength velec:load)) " за " (itoa (- (getvar "MILLISECS") start)) " мс."))
  (c:VELECLOADSELECT))

(defun c:VELECLOADSELECT ()
  (if (and velec:load (> (sslength velec:load) 0))
    (progn
      (setvar "PICKFIRST" 1)
      (sssetfirst nil nil)
      (sssetfirst nil velec:load)
      (princ (strcat "\nВыбрано объектов: " (itoa (sslength velec:load)) "; время построения VST — в строке [XDATAPALETTE] VST.")))
    (princ "\nСначала выполните VELECLOAD."))
  (princ))

(defun c:VELECLOADTOUCH (/ item data)
  (if (and velec:load (setq item (ssname velec:load 7)) (entget item))
    (progn
      (setq data (velec:setvalue (velec:loaddata 7) "L01" '(1000 . "Изменено")))
      (setq item (entget item '("BobrovXDATA")))
      (entmod (subst (list -3 (cons "BobrovXDATA" data)) (assoc -3 item) item))
      (princ (strcat "\nИзменён параметр L01 объекта " (cdr (assoc 5 item)) "; выполните VELECLOADSELECT.")))
    (princ "\nСначала выполните VELECLOAD."))
  (princ))

(princ "\nVELECLOAD — создать и выбрать 1000 объектов × 50 параметров; VELECLOADSELECT — выбрать их снова; VELECLOADTOUCH — изменить объект 7.")
(princ)
